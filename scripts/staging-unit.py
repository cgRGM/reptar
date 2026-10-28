#!/usr/bin/env python3
"""Scoped remote systemd lifecycle. Invoked over SSH with one JSON request; never shell interpolation."""
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import subprocess
import sys
import tarfile
import time

PROPERTIES = ("LoadState", "Description", "ActiveState", "SubState", "MainPID", "Result", "ControlGroup", "MemoryMax", "MemorySwapMax", "TasksMax", "CPUQuotaPerSecUSec", "PrivateNetwork", "NoNewPrivileges", "ProtectSystem", "Restart", "KillMode", "RuntimeMaxUSec", "InvocationID", "OOMPolicy", "User", "ReadWritePaths", "ReadOnlyPaths")


def require(condition, message):
    if not condition:
        raise AssertionError(message)


def command(args):
    return subprocess.run(args, check=True, text=True, capture_output=True, timeout=30).stdout


def validate(request):
    require(request.get("action") in ("read", "prepare", "start", "stop", "purge"), "Unknown lifecycle action")
    require(re.fullmatch(r"reptar-reliability-\d{8}-[a-f0-9]{12}", request.get("runId", "")), "Invalid owned run name")
    require(re.fullmatch(r"[a-f0-9]{32}", request.get("ownerId", "")), "Invalid owner identity")
    require(re.fullmatch(r"[a-f0-9]{64}", request.get("archiveSha256", "")), "Invalid archive hash")
    require(re.fullmatch(r"[a-f0-9]{64}", request.get("helperSha256", "")), "Invalid lifecycle helper pin")
    require(request.get("seconds") in (12, 86400), "Invalid bounded duration")
    require(re.fullmatch(r"[a-f0-9]{40}", request.get("sourceCommit", "")), "Invalid exact native source pin")
    require(0 < request.get("nativeRssKiB", 256 * 1024) <= 256 * 1024, "Invalid native RSS ceiling")
    require(os.getuid() == 1000 and Path.home() == Path("/home/ubuntu"), "Unexpected remote operator identity")
    parent = Path.home() / "reptar-reliability"
    for path in (Path.home(), parent, parent / request["runId"]):
        require(not path.is_symlink(), "Owned path symlink refused")
        if path.exists():
            require(path.is_dir() and path.stat().st_uid == 1000, "Owned path identity mismatch")
    return parent / request["runId"]


def identity(request):
    value = {key: request[key] for key in ("runId", "ownerId", "archiveSha256", "seconds", "sourceCommit", "helperSha256")}
    value["nativeRssKiB"] = request.get("nativeRssKiB", 256 * 1024)
    return value


def inspect_unit(unit):
    result = subprocess.run(["systemctl", "show", unit, *[f"--property={name}" for name in PROPERTIES]], text=True, capture_output=True, timeout=15)
    if result.returncode not in (0, 1, 4):
        raise RuntimeError(result.stderr)
    values = dict(line.split("=", 1) for line in result.stdout.splitlines() if "=" in line)
    require(values.get("LoadState") in ("loaded", "not-found"), "Cannot inspect unit state")
    return values


def inspect(request, root):
    unit = inspect_unit(request["runId"] + ".service")
    if root.exists():
        require((root / "owner.json").is_file() and not (root / "owner.json").is_symlink(), "Existing directory lacks ownership manifest")
        owner = json.loads((root / "owner.json").read_text())
        require(owner == identity(request), "Owned directory belongs to a different resource")
    else:
        require(unit["LoadState"] == "not-found", "Existing unit without owned directory")
        return {"phase": "absent", "root": str(root), "unit": unit}
    if unit["LoadState"] == "loaded":
        require(unit["Description"] == f"Reptar isolated reliability {request['ownerId']}", "Unit ownership mismatch")
    phase = "prepared"
    if (root / "start-attempt.json").exists():
        phase = "running" if unit.get("ActiveState") in ("activating", "active", "deactivating") else "finished-or-failed"
    return {"phase": phase, "root": str(root), "unit": unit}


def admission(root):
    require(shutil.disk_usage(root.parent if root.parent.exists() else Path.home()).free >= 8 * 1024**3, "Insufficient free disk admission headroom")
    fields = dict(line.split(":", 1) for line in Path("/proc/meminfo").read_text().splitlines())
    require(int(fields["MemAvailable"].split()[0]) >= 2 * 1024**2, "Insufficient available memory admission headroom")


def start_command(request, root):
    return ["sudo", "-n", "systemd-run", "--unit", request["runId"], "--description", f"Reptar isolated reliability {request['ownerId']}", "--uid=1000", "--gid=1000", "--service-type=exec", "--working-directory", str(root),
            "--property=CPUQuota=100%", "--property=MemoryMax=512M", "--property=MemorySwapMax=0", "--property=TasksMax=128", "--property=PrivateNetwork=yes", "--property=NoNewPrivileges=yes", "--property=CapabilityBoundingSet=", "--property=ProtectSystem=strict", "--property=ProtectHome=read-only", "--property=PrivateTmp=yes", "--property=PrivateDevices=yes", "--property=ProtectKernelTunables=yes", "--property=ProtectControlGroups=yes", "--property=RestrictSUIDSGID=yes", "--property=OOMPolicy=stop", "--property=Restart=no", "--property=KillMode=control-group", "--property=TimeoutStopSec=10s", f"--property=RuntimeMaxSec={request['seconds'] + 300}s", f"--property=ReadWritePaths={root}", f"--property=ReadOnlyPaths={root / 'bundle'}", "--property=InaccessiblePaths=-/run/docker.sock -/run/containerd/containerd.sock", f"--property=StandardOutput=append:{root / 'manager.log'}", f"--property=StandardError=append:{root / 'manager.log'}", "--setenv=PATH=/usr/bin:/bin", f"--setenv=HOME={root / 'home'}", f"--setenv=TMPDIR={root / 'tmp'}", "/usr/bin/python3.12", "-B", str(root / "bundle/stage-runner.py"), "--bundle", str(root / "bundle"), "--state-dir", str(root / "runtime"), "--seconds", str(request["seconds"]), "--max-native-rss-kib", str(request.get("nativeRssKiB", 256 * 1024))]


def extract(request, root):
    archive = root / "payload.tar"
    require(archive.is_file() and not archive.is_symlink() and archive.stat().st_size <= 256 * 1024**2, "Invalid bundle archive")
    with archive.open("rb") as stream:
        require(hashlib.file_digest(stream, "sha256").hexdigest() == request["archiveSha256"], "Uploaded archive hash mismatch")
    destination = root / "bundle"
    require(not destination.exists(), "Refuse bundle replacement")
    with tarfile.open(archive, "r:") as stream:
        members = stream.getmembers()
        require(len(members) <= 24 and sum(member.size for member in members) <= 256 * 1024**2, "Archive inventory/storage ceiling exceeded")
        for member in members:
            path = PurePosixPath(member.name)
            require(not path.is_absolute() and ".." not in path.parts and member.name not in ("", "."), "Archive traversal refused")
            require(member.isfile() or member.isdir(), "Archive links/devices refused")
        destination.mkdir(mode=0o700)
        require(len({member.name for member in members}) == len(members), "Duplicate archive paths refused")
        stream.extractall(destination, filter="data")
    manifest = json.loads((destination / "manifest.json").read_text())
    require(manifest["sourceCommit"] == request["sourceCommit"], "Uploaded native source pin mismatch")
    require(request["seconds"] != 86400 or manifest["runnerClean"], "Full staging requires a clean runner pin")


def main():
    request = json.loads(sys.argv[1])
    root = validate(request)
    state = inspect(request, root)
    action = request["action"]
    if action == "prepare":
        require(state["phase"] == "absent", "Refuse resource adoption/overwrite")
        admission(root)
        root.parent.mkdir(mode=0o700, exist_ok=True)
        root.mkdir(mode=0o700)
        (root / "owner.json").write_text(json.dumps(identity(request), indent=2))
        for directory in ("home", "tmp"):
            (root / directory).mkdir(mode=0o700)
    elif action == "start":
        require(state["phase"] == "prepared" and not (root / "start-attempt.json").exists(), "Refuse workload restart/retry")
        admission(root)
        extract(request, root)
        (root / "start-attempt.json").write_text(json.dumps({"utc": time.time(), "command": start_command(request, root)}, indent=2))
        command(start_command(request, root))
    elif action in ("stop", "purge"):
        require(state["phase"] != "absent", "Owned resource missing")
        if state["unit"]["LoadState"] == "loaded":
            command(["sudo", "-n", "systemctl", "stop", request["runId"] + ".service"])
        if action == "purge":
            require(request.get("evidenceRetained") is True, "Copy/verify bounded evidence before purge")
            after = inspect_unit(request["runId"] + ".service")
            require(after.get("ActiveState") not in ("active", "activating", "deactivating") and after.get("MainPID", "0") == "0", "Unit not stopped")
            shutil.rmtree(root)
    print(json.dumps(inspect(request, root) if root.exists() else {"phase": "absent", "root": str(root)}))


if __name__ == "__main__":
    main()
