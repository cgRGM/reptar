#!/usr/bin/env python3
"""Run a sealed native bundle inside a separately owned, capped systemd service."""
import argparse
import hashlib
import importlib.util
import json
import math
import os
from pathlib import Path
import signal
import socket
import sys
import threading
import time
from types import SimpleNamespace

PROTOCOL = "reptar-native-staging-v1"
REQUIRED_FILES = {"bun", "operational.py", "stage-runner.py", "probes/acceptance.test.js", "probes/rooms.js"} | {
    f"{app}/{file}" for app in ("switchyard", "rooms", "agent") for file in ("app", "libe_sqlite3.so")
}


def require(condition, message):
    if not condition:
        raise AssertionError(message)


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def verify_bundle(root):
    require(not root.is_symlink(), "Bundle root must not be a symlink")
    root = root.resolve()
    manifest = json.loads((root / "manifest.json").read_text())
    require(manifest["protocol"] == PROTOCOL, "Unknown staging bundle protocol")
    require(set(manifest["sha256"]) == REQUIRED_FILES, "Bundle file inventory mismatch")
    actual = set()
    for path in root.rglob("*"):
        require(not path.is_symlink(), "Bundle symlink refused")
        if path.is_file():
            actual.add(str(path.relative_to(root)))
    require(actual == REQUIRED_FILES | {"manifest.json"}, "Unexpected bundle contents")
    for name, expected in manifest["sha256"].items():
        target = (root / name).resolve()
        require(target.is_relative_to(root) and digest(target) == expected, f"Bundle integrity failed: {name}")
    require(manifest["nativeProducer"]["sourceTree"] == manifest["sourceTree"], "Native source tree mismatch")
    for app in ("switchyard", "rooms", "agent"):
        for name in ("app", "libe_sqlite3.so"):
            key = f"{app}/{name}"
            require(manifest["sha256"][key] == manifest["nativeProducer"]["sha256"][key], "Native producer hash mismatch")
    return manifest


def boundary():
    require(sys.platform == "linux" and os.getuid() == 1000, "Staging requires nonroot Linux UID 1000")
    require({name for _, name in socket.if_nameindex()} == {"lo"}, "Staging network must contain only loopback")
    entry = next(line for line in Path("/proc/self/cgroup").read_text().splitlines() if line.startswith("0::"))
    relative = entry.split("::", 1)[1]
    require(relative.startswith("/system.slice/reptar-reliability-") and relative.endswith(".service"), "Unexpected staging cgroup")
    cgroup = Path("/sys/fs/cgroup") / relative.lstrip("/")
    values = {name: (cgroup / name).read_text().strip() for name in ("cpu.max", "memory.max", "memory.swap.max", "pids.max")}
    quota, period = values["cpu.max"].split()
    require(quota != "max" and 0 < int(quota) <= int(period), "CPU quota exceeds one core")
    require(values["memory.max"] == str(512 * 1024 * 1024), "Missing 512 MiB hard memory cap")
    require(values["memory.swap.max"] == "0" and values["pids.max"] == "128", "Swap/PID cap mismatch")
    mounts = [line.split() for line in Path("/proc/self/mountinfo").read_text().splitlines()]
    require(any(row[4] == "/" and "ro" in row[5].split(",") for row in mounts), "Readonly root filesystem boundary missing")
    status = dict(line.split(":", 1) for line in Path("/proc/self/status").read_text().splitlines() if ":" in line)
    require(status["NoNewPrivs"].strip() == "1" and int(status["CapEff"].strip(), 16) == 0, "Privilege boundary mismatch")
    return {"cgroup": relative, "controls": values, "interfaces": ["lo"], "uid": os.getuid(), "noNewPrivileges": True, "readonlyRoot": True}


def verify_runtime(expected):
    require(expected and all(Path(name).is_absolute() for name in expected), "Runtime pin inventory missing")
    observed = {}
    for name, checksum in expected.items():
        observed[name] = digest(Path(name))
        require(observed[name] == checksum, f"Host runtime changed: {name}")
    return observed


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bundle", type=Path, required=True)
    parser.add_argument("--state-dir", type=Path, required=True)
    parser.add_argument("--seconds", type=int, choices=(12, 86400), required=True)
    parser.add_argument("--max-native-rss-kib", type=int, default=256 * 1024)
    args = parser.parse_args()
    require(0 < args.max_native_rss_kib <= 256 * 1024, "Native RSS ceiling may not be raised")
    manifest = verify_bundle(args.bundle)
    if args.seconds == 86400:
        require(manifest["runnerClean"], "Full staging requires a clean runner source pin")
    isolation = boundary()
    runtime = verify_runtime(manifest["runtimeSha256"])
    root = args.state_dir.resolve()
    require(root.parent.name.startswith("reptar-reliability-") and root.name == "runtime", "Unexpected staging state path")
    root.mkdir(mode=0o700)
    (root / "boundary.json").write_text(json.dumps({"isolation": isolation, "runtimeSha256": runtime, "bundleManifestSha256": digest(args.bundle / "manifest.json"), "startedAt": time.time()}, indent=2))
    evidence = root / "evidence"
    evidence.mkdir(mode=0o700)
    spec = importlib.util.spec_from_file_location("operational", args.bundle / "operational.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    run_args = SimpleNamespace(bun=str(args.bundle / "bun"), switchyard=str(args.bundle / "switchyard/app"), rooms=str(args.bundle / "rooms/app"), agent=str(args.bundle / "agent/app"), seconds=args.seconds, cadence=60 if args.seconds == 86400 else 3, max_native_rss_kib=args.max_native_rss_kib, max_owned_bytes=512 * 1024 * 1024)
    def interrupted(_signal, _frame):
        raise InterruptedError("Staging owner stop/deadline requested")
    previous = {number: signal.signal(number, interrupted) for number in (signal.SIGTERM, signal.SIGINT)}
    watchdog = threading.Timer(args.seconds + 180, lambda: os.kill(os.getpid(), signal.SIGTERM))
    watchdog.daemon = True
    watchdog.start()
    try:
        module.run(run_args, evidence, {"root": args.bundle, "sourceCommit": manifest["sourceCommit"]})
        verify_runtime(manifest["runtimeSha256"])
        result = json.loads((evidence / "result.json").read_text())
        require(result["status"] == "passed" and result["ownedProcessesCleaned"] and result["cycles"] == math.ceil(args.seconds / run_args.cadence), "Incomplete staged result")
        cgroup = Path("/sys/fs/cgroup") / isolation["cgroup"].lstrip("/")
        usage = {name: (cgroup / name).read_text().strip() for name in ("memory.peak", "memory.events", "pids.events", "cpu.stat")}
        memory_events = dict(line.split() for line in usage["memory.events"].splitlines())
        require(memory_events["oom_kill"] == "0", "Cgroup recorded an OOM kill")
        (root / "stage-complete.json").write_text(json.dumps({"status": "passed", "earnedTwentyFourHourElapsed": result["earnedTwentyFourHourElapsed"], "runtimeUnchanged": True, "cgroupUsage": usage, "finishedAt": time.time()}, indent=2))
    finally:
        watchdog.cancel()
        for number, handler in previous.items():
            signal.signal(number, handler)


if __name__ == "__main__":
    main()
