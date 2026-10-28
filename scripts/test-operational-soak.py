#!/usr/bin/env python3
"""Bounded disposable Native AOT operational proving. Echo only; never production data.

Defaults to 7200 actual seconds. A shorter invocation is harness validation, not a soak.
"""
import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import queue
import signal
import shutil
import socket
import sqlite3
import subprocess
import sys
import tempfile
import threading
import time
import urllib.error
import urllib.request

REPO = Path(__file__).resolve().parent.parent


def require(condition, message):
    if not condition:
        raise AssertionError(message)


def http(base, path, body=None, cookie=""):
    headers = {"Content-Type": "application/json"}
    if cookie:
        headers["Cookie"] = cookie
    request = urllib.request.Request(base + path, data=None if body is None else json.dumps(body).encode(), headers=headers)
    with urllib.request.urlopen(request, timeout=10) as response:
        return response.status, response.read().decode(), response.headers


def json_http(base, path, body=None, cookie=""):
    status, text, headers = http(base, path, body, cookie)
    return status, json.loads(text), headers


def free_port():
    with socket.socket() as probe:
        probe.bind(("127.0.0.1", 0))
        return probe.getsockname()[1]


class Owner:
    def __init__(self, root, env):
        self.root, self.env, self.children = root, env, []
        self.sequence = 0
        self.stopped = set()

    def start(self, label, args, env=None, rpc=False, cwd=None):
        self.sequence += 1
        output = (self.root / f"{self.sequence:04}-{label}.log").open("w")
        child = subprocess.Popen(args, cwd=cwd or self.root, env={**self.env, **(env or {})}, start_new_session=True,
                                 stdin=subprocess.PIPE if rpc else subprocess.DEVNULL,
                                 stdout=subprocess.PIPE if rpc else output, stderr=output, text=True, bufsize=1)
        self.children.append((child, output, label))
        self.persist()
        return child

    def persist(self):
        (self.root / "owned-processes.json").write_text(json.dumps([
            {"pid": p.pid, "group": p.pid, "label": label, "exited": p.poll()} for p, _, label in self.children
        ], indent=2))

    @staticmethod
    def alive_group(pid):
        try:
            os.killpg(pid, 0)
            return True
        except ProcessLookupError:
            return False

    def stop(self, child):
        if child in self.stopped:
            return
        if self.alive_group(child.pid):
            os.killpg(child.pid, signal.SIGTERM)
        try:
            child.wait(timeout=5)
        except subprocess.TimeoutExpired:
            os.killpg(child.pid, signal.SIGKILL)
            child.wait(timeout=5)
        if self.alive_group(child.pid):
            os.killpg(child.pid, signal.SIGKILL)
            deadline = time.monotonic() + 3
            while self.alive_group(child.pid) and time.monotonic() < deadline:
                time.sleep(.025)
            require(not self.alive_group(child.pid), f"Owned group {child.pid} survived bounded cleanup")
        self.stopped.add(child)
        for process, output, _ in self.children:
            if process is child:
                output.close()
        if child.stdin:
            child.stdin.close()
        if child.stdout:
            child.stdout.close()
        self.persist()

    def run(self, label, args, env=None, cwd=None):
        child = self.start(label, args, env=env, cwd=cwd)
        try:
            require(child.wait(timeout=45) == 0, f"Probe {label} failed; see owned log")
        finally:
            self.stop(child)

    def cleanup(self):
        failures = []
        for child, output, label in reversed(self.children):
            try:
                self.stop(child)
            except Exception as error:
                failures.append(f"{label}: {error}")
            output.close()
        require(not failures, "; ".join(failures))


class Rpc:
    def __init__(self, owner, binary, workspace, database, session=None):
        args = [str(binary), "--workspace", str(workspace), "--db", str(database), "--provider", "echo", "--mode", "rpc"]
        if session:
            args += ["--session", session]
        self.child = owner.start("agent-rpc", args, rpc=True)
        self.messages = queue.Queue()

        def reader():
            try:
                for line in self.child.stdout:
                    self.messages.put(json.loads(line))
            except Exception as error:
                self.messages.put(error)
        threading.Thread(target=reader, daemon=True).start()

    def call(self, command, **fields):
        tag = str(time.time_ns())
        self.child.stdin.write(json.dumps({"type": command, "id": tag, **fields}) + "\n")
        self.child.stdin.flush()
        deadline = time.monotonic() + 10
        while time.monotonic() < deadline:
            event = self.messages.get(timeout=max(.01, deadline - time.monotonic()))
            if isinstance(event, Exception):
                raise event
            if event.get("type") == "response" and event.get("id") == tag:
                require(event.get("success"), f"RPC {command} failed")
                return event["data"]
        raise TimeoutError(f"RPC {command}")


def ready(child, base, path):
    deadline = time.monotonic() + 15
    while time.monotonic() < deadline:
        require(child.poll() is None, "Owned native server exited before readiness")
        try:
            if json_http(base, path)[0] == 200:
                return
        except (OSError, urllib.error.URLError):
            pass
        time.sleep(.05)
    raise TimeoutError(f"Readiness failed for owned {base}")


def rooms_idle(base):
    deadline = time.monotonic() + 2
    while True:
        stats = json_http(base, "/stats")[1]
        require(stats["messagesPersistFailed"] == 0 and stats["messagesPersistDropped"] == 0 and stats["fanoutFailures"] == 0, "Rooms failed/dropped work: " + json.dumps(stats))
        if all(stats[key] == 0 for key in ("persisterQueueDepth", "persisterActiveWrites", "activeConnections", "rooms")):
            return stats
        require(time.monotonic() < deadline, "Rooms retained outstanding peer/write work: " + json.dumps(stats))
        time.sleep(.025)


def rss_kib(children):
    result = {}
    for name, child in children.items():
        require(child.poll() is None, f"Long-lived {name} exited")
        output = subprocess.run(["ps", "-o", "rss=", "-p", str(child.pid)], capture_output=True, text=True, timeout=3, check=True)
        result[name] = int(output.stdout.strip())
    return result


def directory_bytes(root):
    return sum(file.stat().st_size for file in root.rglob("*") if file.is_file())


def snapshots(root, databases):
    evidence = {}
    destination = root / "backup-restored"
    destination.mkdir()
    for name, source in databases.items():
        path = destination / f"{name}.db"
        deadline = time.monotonic() + 10
        with sqlite3.connect(source, timeout=3) as active, sqlite3.connect(path, timeout=3) as backup:
            def progress(_status, _remaining, _total):
                if time.monotonic() >= deadline:
                    raise TimeoutError("SQLite backup deadline")
            active.backup(backup, pages=128, progress=progress, sleep=.01)
        with sqlite3.connect(path, timeout=3) as restored:
            require(restored.execute("PRAGMA integrity_check").fetchone()[0] == "ok", "Backup integrity failure")
            require(restored.execute("PRAGMA foreign_key_check").fetchall() == [], "Backup foreign key failure")
            tables = [row[0] for row in restored.execute("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'")]
            counts = {table: restored.execute('SELECT COUNT(*) FROM "' + table.replace('"', '""') + '"').fetchone()[0] for table in tables}
        evidence[name] = {"path": str(path), "counts": counts, "bytes": path.stat().st_size}
    return evidence


def run(args, root, prepared=None):
    workspace = root / "workspace"
    workspace.mkdir()
    (workspace / "README.md").write_text("Disposable offline reliability workspace.\n")
    home = root / "home"
    home.mkdir()
    env = {key: os.environ[key] for key in ("PATH", "TMPDIR", "LANG") if key in os.environ}
    env.update({"HOME": str(home), "XDG_CONFIG_HOME": str(home / ".config"), "HOST": "127.0.0.1", "SWITCHYARD_INSECURE_LOCAL": "1"})
    owner = Owner(root, env)
    ports = {name: free_port() for name in ("switchyard", "rooms", "agent")}
    require(len(set(ports.values())) == 3, "Port selection collision; refuse rather than replace a listener")
    bases = {name: f"http://127.0.0.1:{port}" for name, port in ports.items()}
    databases = {name: root / f"{name}.db" for name in ("switchyard", "rooms", "agent-web", "agent-rpc")}
    binaries = {}
    native_hashes = {}
    artifact_bytes = 0
    for name in ("switchyard", "rooms", "agent"):
        supplied = Path(getattr(args, name)).resolve()
        require(supplied.is_file() and os.access(supplied, os.X_OK), "Published executable missing")
        files = [supplied] + [supplied.parent / sidecar for sidecar in ("libe_sqlite3.so", "libe_sqlite3.dylib") if (supplied.parent / sidecar).is_file()]
        require(len(files) > 1, "Published SQLite native sidecar missing")
        artifact_bytes += sum(file.stat().st_size for file in files)
        require(artifact_bytes <= args.max_owned_bytes, "Native artifacts exceed owned storage admission budget")
        owned = root / "artifacts" / name
        owned.mkdir(parents=True)
        for file in files:
            shutil.copy2(file, owned / file.name)
        binaries[name] = str(owned / supplied.name)
        native_hashes[name] = {file.name: hashlib.sha256(file.read_bytes()).hexdigest() for file in owned.iterdir()}
    manifest = {"scope": "isolated-staging-NativeAOT-Echo-only" if prepared else "local-disposable-NativeAOT-Echo-only", "sourceCommit": prepared["sourceCommit"] if prepared else subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=REPO, text=True).strip(),
                "startedAt": time.time(), "requestedSeconds": args.seconds, "cadenceSeconds": args.cadence, "ports": ports,
                "binarySha256": {name: hashlib.sha256(Path(path).read_bytes()).hexdigest() for name, path in binaries.items()}, "nativeFilesSha256": native_hashes,
                "runnerSha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(), "bunSha256": hashlib.sha256(Path(args.bun).read_bytes()).hexdigest(),
                "bounds": {"maxCycles": math.ceil(args.seconds / args.cadence), "nativeRssKiB": args.max_native_rss_kib, "ownedStorageBytes": args.max_owned_bytes, "ownerStopDeadlineSeconds": args.seconds + 180, "maxTotalSecondsIncludingCleanup": args.seconds + 300, "probeTimeoutSeconds": 45, "provider": "echo", "paidCalls": 0},
                "limits": ["not multi-release maturity", "not remote/D1 proving", "process recovery is not power-loss testing", "RSS is sampled for long-lived native processes, not a hard OS allocation limit"]}
    (root / "manifest.json").write_text(json.dumps(manifest, indent=2))
    result = {"status": "failed", "cycles": 0, "earnedTwoHourElapsed": False}
    try:
        probes = root / "probes"
        probes.mkdir()
        for label, source, target in (("switchyard", "examples/switchyard/acceptance.test.ts", "acceptance.test.js"), ("rooms", "examples/realtime-rooms/smoke.ts", "rooms.js")):
            if prepared:
                shutil.copy2(prepared["root"] / "probes" / target, probes / target)
            else:
                owner.run(f"bundle-{label}", [args.bun, "build", source, "--target=bun", "--outfile", str(probes / target)], cwd=REPO)
        manifest["probeSha256"] = {file.name: hashlib.sha256(file.read_bytes()).hexdigest() for file in probes.iterdir()}
        (root / "manifest.json").write_text(json.dumps(manifest, indent=2))
        children = {}
        children["switchyard"] = owner.start("switchyard", [binaries["switchyard"]], {"PORT": str(ports["switchyard"]), "SWITCHYARD_DB": str(databases["switchyard"])})
        children["rooms"] = owner.start("rooms", [binaries["rooms"]], {"PORT": str(ports["rooms"]), "REPTAR_ROOMS_DB": str(databases["rooms"])})
        agent_base = [binaries["agent"], "--workspace", str(workspace), "--db", str(databases["agent-web"]), "--provider", "echo"]
        children["agent-web"] = owner.start("agent-web", agent_base + ["--web", str(ports["agent"])])
        ready(children["switchyard"], bases["switchyard"], "/health")
        ready(children["rooms"], bases["rooms"], "/health")
        ready(children["agent-web"], bases["agent"], "/api/health")
        rpc = Rpc(owner, Path(binaries["agent"]), workspace, databases["agent-rpc"])
        children["agent-rpc"] = rpc.child
        _, session, _ = json_http(bases["agent"], "/api/sessions", {"title": "operational sentinel"})
        session_id = session["sessionId"]
        _, _, signup_headers = json_http(bases["switchyard"], "/api/auth/signup", {"email": "recovery-sentinel@example.test", "name": "Recovery sentinel", "password": "Disposable-soak-2026!"})
        cookie = signup_headers.get("Set-Cookie").split(";", 1)[0]
        _, sentinel_project, _ = json_http(bases["switchyard"], "/api/projects", {"name": "operational-restoration-sentinel"}, cookie)
        started = time.monotonic()
        deadline = started + args.seconds
        cursor = None
        rpc_session = None
        peak_rss = {}
        previous_cycle = time.time()
        while time.monotonic() < deadline:
            cycle = result["cycles"]
            require(hashlib.sha256(Path(args.bun).read_bytes()).hexdigest() == manifest["bunSha256"], "Bun executable changed during proving")
            require(time.time() - previous_cycle <= args.cadence + 90, "Long observation gap/suspend invalidates continuous proving")
            previous_cycle = time.time()
            owner.run(f"switchyard-{cycle}", [args.bun, "test", str(probes / "acceptance.test.js")], {"SWITCHYARD_URL": bases["switchyard"]}, probes)
            owner.run(f"rooms-{cycle}", [args.bun, str(probes / "rooms.js")], {"BENCH_HOST": bases["rooms"], "BENCH_WS_URL": bases["rooms"].replace("http:", "ws:")}, probes)
            _, stream, _ = http(bases["agent"], "/api/chat", {"provider": "echo", "sessionId": session_id, "message": f"offline operational turn {cycle}"})
            events = [json.loads(line[5:].strip()) for line in stream.splitlines() if line.startswith("data:") and line[5:].strip() != "[DONE]"]
            require(any(event.get("type") == "done" for event in events) and not any(event.get("type") == "error" for event in events), "Agent web stream did not finish cleanly")
            require(json_http(bases["agent"], f"/api/chat/status/{session_id}")[1]["running"] is False, "Agent web retained a run reservation")
            history = json_http(bases["agent"], f"/api/sessions/{session_id}/messages")[1]
            require(len(history) == (cycle + 1) * 2 and [message["role"] for message in history[-2:]] == ["user", "assistant"], "Agent web history missing/duplicated a turn")
            rpc.call("prompt", message=f"offline operational RPC turn {cycle}")
            wait = time.monotonic() + 10
            while True:
                entries = rpc.call("get_entries")["entries"]
                if len(entries) >= (cycle + 1) * 2:
                    break
                require(time.monotonic() < wait, "RPC turn did not persist within deadline")
                time.sleep(.025)
            require(len(entries) == (cycle + 1) * 2, "RPC duplicate/missing history")
            if cursor:
                require(len(rpc.call("get_entries", since=cursor)["entries"]) == 2, "RPC replay cursor duplicated a turn")
            cursor = entries[-1]["id"]
            state = rpc.call("get_state")
            settled = time.monotonic() + 5
            while state["isStreaming"]:
                require(time.monotonic() < settled, "RPC retained an active run after persistence")
                time.sleep(.025)
                state = rpc.call("get_state")
            rpc_session = state["sessionId"]
            stats = rooms_idle(bases["rooms"])
            result["lastObservedRooms"] = stats
            rss = rss_kib(children)
            result["lastObservedRssKiB"] = rss
            require(sum(rss.values()) <= args.max_native_rss_kib, "Native RSS exceeded observation budget")
            for name, amount in rss.items():
                peak_rss[name] = max(peak_rss.get(name, 0), amount)
            size = directory_bytes(root)
            result["lastObservedStorageBytes"] = size
            require(size <= args.max_owned_bytes, "Owned storage exceeded budget")
            result["cycles"] += 1
            with (root / "observations.jsonl").open("a") as observations:
                observations.write(json.dumps({"cycle": cycle, "utc": time.time(), "elapsedSeconds": time.monotonic() - started, "rssKiB": rss, "ownedBytes": size, "rooms": stats, "agentWebMessages": len(history), "agentRpcEntries": len(entries)}) + "\n")
            pause = min(deadline - time.monotonic(), started + result["cycles"] * args.cadence - time.monotonic())
            if pause > 0:
                time.sleep(pause)
        elapsed = time.monotonic() - started
        require(time.time() - previous_cycle <= args.cadence + 90, "Final observation gap invalidates continuous proving")
        require(result["cycles"] == math.ceil(args.seconds / args.cadence), "Scheduled cycle count missing or exceeded")
        backup = snapshots(root, databases)
        require(backup["rooms"]["counts"]["room_messages"] == result["cycles"] * 2, "Rooms backup lost/duplicated snapshots")
        for child in children.values():
            owner.stop(child)
        # Cold native processes read actual SQLite backups, not just filesystem copies or row counts.
        restored_switch = owner.start("restored-switchyard", [binaries["switchyard"]], {"PORT": str(ports["switchyard"]), "SWITCHYARD_DB": backup["switchyard"]["path"]})
        ready(restored_switch, bases["switchyard"], "/health")
        projects = json_http(bases["switchyard"], "/api/projects", cookie=cookie)[1]
        require(any(project["id"] == sentinel_project["id"] for project in projects), "Restored owner could not read sentinel project")
        with sqlite3.connect(backup["rooms"]["path"], timeout=3) as restored_room_store:
            saved_id, saved_room = restored_room_store.execute("SELECT id, room_id FROM room_messages LIMIT 1").fetchone()
        restored_rooms = owner.start("restored-rooms", [binaries["rooms"]], {"PORT": str(ports["rooms"]), "REPTAR_ROOMS_DB": backup["rooms"]["path"]})
        ready(restored_rooms, bases["rooms"], "/health")
        restored_history = json_http(bases["rooms"], f"/rooms/{saved_room}/messages?limit=10")[1]
        require(any(message["id"] == saved_id for message in restored_history), "Cold native Rooms could not read restored snapshot")
        restored_agent = owner.start("restored-agent", [binaries["agent"], "--workspace", str(workspace), "--db", backup["agent-web"]["path"], "--provider", "echo", "--web", str(ports["agent"])])
        ready(restored_agent, bases["agent"], "/api/health")
        require(len(json_http(bases["agent"], f"/api/sessions/{session_id}/messages")[1]) == result["cycles"] * 2, "Restored web history changed")
        restored_rpc = Rpc(owner, Path(binaries["agent"]), workspace, Path(backup["agent-rpc"]["path"]), rpc_session)
        require(len(restored_rpc.call("get_entries")["entries"]) == result["cycles"] * 2, "Restored RPC history changed")
        require(restored_rpc.call("get_entries", since=cursor)["entries"] == [], "Restored RPC duplicated replay")
        result.update({"status": "passed", "actualElapsedSeconds": elapsed, "earnedTwoHourElapsed": not prepared and elapsed >= 7200 and args.seconds >= 7200, "nativePeakRssKiB": peak_rss, "backupRecovery": backup,
                       "scope": "two-hour-local-proving" if not prepared and elapsed >= 7200 and args.seconds >= 7200 else "short-harness-validation-only"})
        if prepared:
            result["earnedTwentyFourHourElapsed"] = args.seconds == 86400 and elapsed >= 86400
            result["scope"] = "24-hour-isolated-staging" if result["earnedTwentyFourHourElapsed"] else "short-isolated-staging-control-only"
    except BaseException as error:
        result["error"] = f"{type(error).__name__}: {error}"
        raise
    finally:
        for number in (signal.SIGTERM, signal.SIGINT):
            signal.signal(number, signal.SIG_IGN)
        try:
            owner.cleanup()
            result["ownedProcessesCleaned"] = True
        except Exception as error:
            result.update({"status": "failed", "cleanupError": str(error), "ownedProcessesCleaned": False})
            raise
        finally:
            result["finishedAt"] = time.time()
            (root / "result.json").write_text(json.dumps(result, indent=2))
            print(json.dumps({"root": str(root), **result}, indent=2))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("switchyard", "rooms", "agent"):
        parser.add_argument(f"--{name}", required=True)
    parser.add_argument("--bun", default="bun")
    parser.add_argument("--seconds", type=float, default=7200)
    parser.add_argument("--cadence", type=float, default=60)
    parser.add_argument("--max-native-rss-kib", type=int, default=1024 * 1024)
    parser.add_argument("--max-owned-bytes", type=int, default=128 * 1024 * 1024)
    args = parser.parse_args()
    args.bun = shutil.which(args.bun)
    require(args.bun is not None, "Bun executable missing")
    args.bun = str(Path(args.bun).resolve())
    require(sys.platform in ("darwin", "linux"), "Owned POSIX process groups require macOS/Linux")
    require(0 < args.seconds <= 7200 and 1 <= args.cadence <= 60, "Invalid bounded duration/cadence")
    require(args.seconds >= 7200 or args.seconds <= 120, "Short validation is <=120s; full proving is 7200s")
    require(args.seconds < 7200 or args.cadence == 60, "Full local proving is bounded to one cycle per minute")
    require(0 < args.max_native_rss_kib <= 1024 * 1024 and 0 < args.max_owned_bytes <= 128 * 1024 * 1024, "Resource limits may only lower the bounded ceiling")
    dirty = subprocess.check_output(["git", "status", "--porcelain"], cwd=REPO, text=True).strip()
    require(args.seconds < 7200 or not dirty, "Full proving requires a clean pinned source revision")
    root = Path(tempfile.mkdtemp(prefix="reptar-reliability-soak-"))
    os.chmod(root, 0o700)
    print(f"Owned operational evidence: {root}", flush=True)
    def interrupted(_signal, _frame):
        raise InterruptedError("Operational owner stop/deadline requested")
    previous = {number: signal.signal(number, interrupted) for number in (signal.SIGTERM, signal.SIGINT)}
    watchdog = threading.Timer(args.seconds + 180, lambda: os.kill(os.getpid(), signal.SIGTERM))
    watchdog.daemon = True
    watchdog.start()
    try:
        run(args, root)
    finally:
        watchdog.cancel()
        for number, handler in previous.items():
            signal.signal(number, handler)


if __name__ == "__main__":
    main()
