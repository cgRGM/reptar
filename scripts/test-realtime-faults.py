#!/usr/bin/env python3
"""Run real-SQLite native Rooms persistence fault checkpoints in disposable state."""
import hashlib
import json
from pathlib import Path
import shutil
import signal
import subprocess
import sys
import tempfile
import time


def crash_checkpoint(binary, root):
    checkpoint = root / "admitted.txt"
    with (root / "crash-producer.log").open("w") as output:
        child = subprocess.Popen([str(binary), str(root), "crash-producer"], cwd=root, stdout=output, stderr=subprocess.STDOUT)
        try:
            deadline = time.monotonic() + 10
            while not checkpoint.exists() or checkpoint.read_text() != "ADMITTED_NOT_COMMITTED:admitted":
                if child.poll() is not None:
                    raise AssertionError("Producer exited before the uncommitted-admission checkpoint")
                if time.monotonic() >= deadline:
                    raise TimeoutError("Producer checkpoint exceeded 10s")
                time.sleep(0.01)
            child.kill()  # This exact owned PID; never a port/name/global process match.
            if child.wait(timeout=3) != -signal.SIGKILL:
                raise AssertionError("Owned producer did not terminate with SIGKILL")
        finally:
            if child.poll() is None:
                child.kill()
                child.wait(timeout=3)
    verified = subprocess.run([str(binary), str(root), "verify-loss"], cwd=root, capture_output=True, text=True, timeout=10)
    if verified.returncode != 0 or not verified.stdout.startswith("PASS owned SIGKILL"):
        raise AssertionError(f"Crash verification failed: {verified.stdout}\n{verified.stderr}")
    print(verified.stdout, end="")


def main():
    if sys.platform not in ("darwin", "linux"):
        raise RuntimeError("This owned-SIGKILL gate requires macOS or Linux")
    binary = Path(sys.argv[1]).resolve()
    root = Path(tempfile.mkdtemp(prefix="reptar-rooms-faults-"))
    try:
        result = subprocess.run([str(binary), str(root)], cwd=root, capture_output=True, text=True, timeout=45)
        print(result.stdout, end="")
        if result.returncode != 0 or not result.stdout.startswith("PASS real SQLite"):
            raise AssertionError(f"Native Rooms fault gate failed ({result.returncode}):\n{result.stderr}")
        crash_checkpoint(binary, root)
        print(json.dumps({"result": "passed", "binarySha256": hashlib.sha256(binary.read_bytes()).hexdigest(), "checks": ["bounded-admission-versus-commit", "observable-overflow-and-active-work", "stop-rejects-new-admission-and-drains", "real-SQLite-trigger-failure-and-continuation", "stable-storage-identities", "128-record-module-drain-before-database-dispose", "reopen-and-integrity-check", "owned-SIGKILL-proves-uncommitted-admission-is-not-durable"], "limits": ["broadcast receipt is not durable acknowledgement", "queued work can be lost on SIGKILL", "process crash is not power-loss testing", "custom module hooks have no global timeout"]}, indent=2))
    except BaseException:
        print(f"Retaining owned failure state: {root}", file=sys.stderr)
        raise
    else:
        shutil.rmtree(root)


if __name__ == "__main__":
    main()
