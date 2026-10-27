#!/usr/bin/env python3
"""Published native queue retry, cancellation, shutdown drain and deadline gate."""
import hashlib
import http.client
import json
import os
from pathlib import Path
import socket
import sqlite3
import subprocess
import sys
import tempfile
import time
import uuid


def require(condition, message):
    if not condition:
        raise AssertionError(message)


def wait_for(predicate, server, label, seconds=10):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        if predicate():
            return
        require(server.poll() is None, f"Server exited while waiting for {label}")
        time.sleep(0.02)
    raise AssertionError(f"Deadline exceeded for {label}")


def state(root):
    with sqlite3.connect(root / "queue.db", timeout=1) as connection:
        return connection.execute("SELECT state, attempts FROM reptar_jobs").fetchone()


def effects(root):
    with sqlite3.connect(root / "queue.db") as connection:
        return connection.execute("SELECT COUNT(*) FROM effects").fetchone()[0]


class Server:
    def __init__(self, binary, root, mode):
        self.root = root
        root.mkdir(parents=True, exist_ok=True)
        self.log = (root / f"{mode}.log").open("w+")
        identity = uuid.uuid4().hex
        with socket.socket() as probe:
            probe.bind(("127.0.0.1", 0))
            port = probe.getsockname()[1]
        environment = {**os.environ, "HOST": "127.0.0.1", "PORT": str(port), "REPTAR_PROBE_RUN_ID": identity, "REPTAR_LIFECYCLE_ROOT": str(root), "REPTAR_LIFECYCLE_MODE": mode, "REPTAR_QUEUE_POLL_SECS": "1", "REPTAR_BACKGROUND_SHUTDOWN_SECS": "1" if mode == "ignore" else "5", "REPTAR_SQLITE_JOURNAL_MODE": "WAL", "REPTAR_SQLITE_SYNCHRONOUS": "NORMAL"}
        self.process = subprocess.Popen([str(binary)], cwd=root, env=environment, stdout=self.log, stderr=self.log)
        def ready():
            connection = http.client.HTTPConnection("127.0.0.1", port, timeout=1)
            try:
                connection.request("GET", "/health")
                response = connection.getresponse()
                return response.status == 200 and response.read().decode() == identity
            except (OSError, http.client.HTTPException):
                return False
            finally:
                connection.close()
        try:
            wait_for(ready, self.process, "owned readiness")
        except BaseException:
            self.close()
            raise

    def close(self):
        if self.process.poll() is None:
            self.process.kill()
        self.process.wait(timeout=5)
        self.log.flush()
        self.log.close()


def check_drain(binary, root):
    server = Server(binary, root, "drain")
    try:
        wait_for(lambda: (root / "handler-started").exists(), server.process, "queue handler")
        server.process.terminate()
        time.sleep(0.2)
        require(server.process.poll() is None, "Host returned before its in-flight queue handler drained")
        require(not (root / "module-stop").exists(), "Module/database disposed before queue drain")
        (root / "release").write_text("continue")
        require(server.process.wait(timeout=8) == 0, "Graceful drain failed")
        require(state(root)[0] == "completed" and effects(root) == 1, "Drained work was not committed/acked")
        require((root / "module-stop").read_text() == "workers-stopped", "Module teardown preceded queue completion")
    finally:
        server.close()


def check_cancel(binary, root):
    server = Server(binary, root, "cancel")
    try:
        wait_for(lambda: (root / "handler-started").exists() and (root / "cron-started").exists(), server.process, "queue and cron handlers")
        server.process.terminate()
        require(server.process.wait(timeout=8) == 0, "Cooperative cancellation failed")
        require((root / "handler-stopped").exists() and (root / "cron-stopped").exists(), "Shutdown token did not reach both handlers")
        require((root / "module-stop").read_text() == "workers-stopped", "Modules disposed while cancelled workers were active")
        require(state(root) == ("pending", 0), "Shutdown falsely acknowledged or consumed retry budget for unfinished work")
    finally:
        server.close()


def check_retry(binary, root):
    server = Server(binary, root, "retry")
    try:
        wait_for(lambda: state(root)[0] == "completed", server.process, "retry completion")
        require(state(root)[1] == 1 and effects(root) == 1, "Retry attempts/effects were not preserved")
        server.process.terminate()
        require(server.process.wait(timeout=8) == 0, "Retry worker did not stop")
    finally:
        server.close()


def check_crash(binary, root):
    server = Server(binary, root, "crash")
    try:
        wait_for(lambda: (root / "handler-started").exists(), server.process, "committed side effect")
        require(effects(root) == 1 and state(root)[0] == "pending", "Crash checkpoint is not between effect and ack")
        server.process.kill()
        server.process.wait(timeout=5)
    finally:
        server.close()
    resumed = Server(binary, root, "complete")
    try:
        wait_for(lambda: state(root)[0] == "completed", resumed.process, "redelivery after crash")
        require(effects(root) == 1, "Application idempotency key failed on redelivery")
        resumed.process.terminate()
        require(resumed.process.wait(timeout=8) == 0, "Resumed worker failed shutdown")
    finally:
        resumed.close()


def check_deadline(binary, root):
    server = Server(binary, root, "ignore")
    try:
        wait_for(lambda: (root / "handler-started").exists(), server.process, "noncooperative handler")
        started = time.monotonic()
        server.process.terminate()
        require(server.process.wait(timeout=5) != 0, "Expired worker drain must fail, not claim graceful success")
        require(time.monotonic() - started < 4, "Shutdown deadline was not bounded")
        server.log.flush()
        require("Background drain expired" in (root / "ignore.log").read_text(), "Host did not report its drain deadline failure")
        require(not (root / "module-stop").exists(), "Resources were disposed beneath an unfinished worker")
        require(state(root) == ("pending", 0), "Timed-out work was falsely acknowledged")
    finally:
        server.close()


def main():
    binary = Path(sys.argv[1]).resolve()
    with tempfile.TemporaryDirectory(prefix="reptar-background-") as directory:
        root = Path(directory)
        try:
            check_drain(binary, root / "drain")
            check_cancel(binary, root / "cancel")
            check_retry(binary, root / "retry")
            check_crash(binary, root / "crash")
            check_deadline(binary, root / "deadline")
        except BaseException:
            for log in root.glob("*/*.log"):
                print(f"--- {log.name} ---\n{log.read_text()}", file=sys.stderr)
            raise
        print(json.dumps({"result": "passed", "binarySha256": hashlib.sha256(binary.read_bytes()).hexdigest(), "checks": ["drain-before-dispose", "queue-and-cron-cancellation", "retry-budget", "crash-redelivery-with-application-idempotency", "bounded-noncooperative-shutdown"], "limits": ["single poller only", "at least once, not exactly once"]}, indent=2))


if __name__ == "__main__":
    main()
