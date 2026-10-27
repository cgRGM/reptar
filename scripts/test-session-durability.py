#!/usr/bin/env python3
"""Cold-process, multi-process and SIGKILL session-store invariants; no model/network."""
import hashlib
import json
import os
from pathlib import Path
import selectors
import shutil
import sqlite3
import subprocess
import sys
import tempfile
import time


def require(condition, message):
    if not condition:
        raise AssertionError(message)


def environment():
    return {**os.environ, "REPTAR_SQLITE_JOURNAL_MODE": "WAL", "REPTAR_SQLITE_SYNCHRONOUS": "NORMAL"}


def invoke(binary, directory, mode, db, session, *args):
    result = subprocess.run([str(binary), mode, str(db), session, *map(str, args)], cwd=directory, env=environment(), capture_output=True, text=True, timeout=30)
    require(result.returncode == 0, f"{mode} failed: {result.stderr}\n{result.stdout}")
    return result.stdout.splitlines()


def integrity(db):
    with sqlite3.connect(db) as connection:
        require(connection.execute("PRAGMA integrity_check").fetchone() == ("ok",), "SQLite integrity check failed")


def check_concurrent(binary, directory):
    db = Path(directory) / "concurrent.db"
    invoke(binary, directory, "verify", db, "shared")  # Initialize schema before concurrent opens.
    processes = [subprocess.Popen([str(binary), "append", str(db), "shared", "64"], cwd=directory, env=environment(), stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True) for _ in range(2)]
    try:
        acknowledgements = []
        for process in processes:
            stdout, stderr = process.communicate(timeout=30)
            require(process.returncode == 0, f"Concurrent append failed: {stderr}")
            lines = stdout.splitlines()
            require(len(lines) == 64, f"Expected 64 acknowledged appends, got {len(lines)}")
            acknowledgements.extend(lines)
        actual = invoke(binary, directory, "verify", db, "shared")
        require(len(actual) == 128 and set(actual) == set(acknowledgements), "Concurrent acknowledged history was duplicated or lost")
        integrity(db)
        return len(actual)
    finally:
        for process in processes:
            if process.poll() is None:
                process.kill()
            process.wait(timeout=5)


def acknowledged_prefix(process, count):
    acknowledgements = []
    pending = b""
    deadline = time.monotonic() + 10
    with selectors.DefaultSelector() as selector:
        selector.register(process.stdout, selectors.EVENT_READ)
        while len(acknowledgements) < count:
            remaining = deadline - time.monotonic()
            require(remaining > 0 and selector.select(remaining), "Writer did not acknowledge within its deadline")
            chunk = os.read(process.stdout.fileno(), 4096)
            require(chunk, "Writer exited before required acknowledgements")
            pending += chunk
            while b"\n" in pending and len(acknowledgements) < count:
                line, pending = pending.split(b"\n", 1)
                acknowledgements.append(line.decode().strip())
    return acknowledgements


def check_crash(binary, directory, checkpoint):
    db = Path(directory) / f"crash-{checkpoint}.db"
    process = subprocess.Popen([str(binary), "append", str(db), "crash", "1000000"], cwd=directory, env=environment(), stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    try:
        acknowledgements = acknowledged_prefix(process, checkpoint)
        require(process.poll() is None, "Crash probe completed before SIGKILL")
        process.kill()  # Deliberately bypass managed disposal/graceful shutdown.
        process.wait(timeout=5)
        actual = invoke(binary, directory, "verify", db, "crash")
        require(set(acknowledgements).issubset(actual), "An acknowledged append disappeared after SIGKILL")
        require(len(actual) == len(set(actual)), "Crash recovery duplicated entries")
        integrity(db)
        resumed = invoke(binary, directory, "append", db, "crash", 1)
        after = invoke(binary, directory, "verify", db, "crash")
        require(after == actual + resumed, "Append after crash did not continue the committed chain")
        return {"acknowledgedBeforeKill": checkpoint, "committedAfterRecovery": len(actual)}
    finally:
        if process.poll() is None:
            process.kill()
        process.wait(timeout=5)
        process.stdout.close()
        process.stderr.close()


def check_fork(binary, directory):
    db = Path(directory) / "fork.db"
    source = invoke(binary, directory, "append", db, "source", 8)
    invoke(binary, directory, "fork", db, "source", "target")
    target = invoke(binary, directory, "verify", db, "target")
    require(len(target) == len(source) and not set(target).intersection(source), "Fork failed to copy into distinct immutable entries")
    invoke(binary, directory, "append", db, "target", 1)
    require(invoke(binary, directory, "verify", db, "source") == source, "Fork mutation changed its source")
    with sqlite3.connect(db) as connection:
        original = connection.execute("SELECT content FROM agent_session_entries WHERE session_id='source' ORDER BY seq").fetchall()
        copied = connection.execute("SELECT content FROM agent_session_entries WHERE session_id='target' ORDER BY seq LIMIT 8").fetchall()
        require(copied == original, "Fork did not preserve the original messages")
    integrity(db)


def check_upgrade(binary, previous, directory):
    db = Path(directory) / "upgrade.db"
    invoke(previous, directory, "seed-flat", db, "legacy")
    with sqlite3.connect(db) as connection:
        old_rows = connection.execute("SELECT * FROM agent_messages ORDER BY rowid").fetchall()
        require(len(old_rows) == 2, "Previous-version fixture did not seed two flat messages")
        backup = Path(directory) / "backup.db"
        with sqlite3.connect(backup) as destination:
            connection.backup(destination)
    require(invoke(binary, directory, "import", db, "legacy") == ["2"], "Legacy upgrade did not import both records")
    imported = invoke(binary, directory, "verify", db, "legacy")
    require(len(imported) == 2, "Upgrade lost legacy history")
    require(invoke(binary, directory, "import", db, "legacy") == ["0"], "Upgrade replay duplicated history")
    with sqlite3.connect(db) as connection:
        require(connection.execute("SELECT * FROM agent_messages ORDER BY rowid").fetchall() == old_rows, "Upgrade mutated legacy source data")
    # Restore to a different file, never overwrite the live candidate database.
    restored = Path(directory) / "restored.db"
    shutil.copyfile(backup, restored)
    require(invoke(binary, directory, "import", restored, "legacy") == ["2"], "Backup restore could not be upgraded")
    require(len(invoke(binary, directory, "verify", restored, "legacy")) == 2, "Restored backup lost history")
    integrity(db)
    integrity(restored)
    return {"previousBinarySha256": hashlib.sha256(previous.read_bytes()).hexdigest(), "legacyRows": 2, "backupRestoreVerified": True}


def main():
    binary = Path(sys.argv[1]).resolve()
    previous = Path(sys.argv[2]).resolve() if len(sys.argv) > 2 else None
    with tempfile.TemporaryDirectory(prefix="reptar-session-durability-") as directory:
        concurrent = check_concurrent(binary, directory)
        crashes = [check_crash(binary, directory, checkpoint) for checkpoint in (1, 7, 19)]
        check_fork(binary, directory)
        # Current-to-current legacy-format recovery always runs. A pinned previous
        # binary additionally proves version compatibility; never infer it otherwise.
        upgrade = check_upgrade(binary, previous or binary, directory)
        print(json.dumps({"result": "passed", "binarySha256": hashlib.sha256(binary.read_bytes()).hexdigest(), "concurrentAcknowledgedEntries": concurrent, "crashTrials": crashes, "upgrade": upgrade, "pinnedPreviousVersionUsed": previous is not None, "limits": ["process crash, not power loss", "SQLite WAL/NORMAL", "no exactly-once guarantee for unacknowledged writes"]}, indent=2))


if __name__ == "__main__":
    main()
