#!/usr/bin/env python3
"""Seal native CI artifacts, pinned Linux Bun, probes and staging runner. No remote mutations."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tarfile

REPO = Path(__file__).resolve().parent.parent
BUN_SHA256 = "3632a4fae998deaad2358e22ae74d1fd6c673b9be760a470e3a0044267cb4438"


def require(condition, message):
    if not condition:
        raise AssertionError(message)


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def git(*args):
    return subprocess.check_output(["git", *args], cwd=REPO, text=True).strip()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--native", type=Path, required=True)
    parser.add_argument("--source-commit", required=True)
    parser.add_argument("--bun-linux", type=Path, required=True)
    parser.add_argument("--runtime-pin", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--control", action="store_true")
    args = parser.parse_args()
    require(not args.output.exists() and not args.output.with_suffix(".tar").exists(), "Refuse bundle overwrite")
    native = json.loads((args.native / "manifest.json").read_text())
    source_commit = git("rev-parse", args.source_commit)
    require(native["sourceTree"] == git("rev-parse", f"{source_commit}^{{tree}}"), "Native CI source tree mismatch")
    require(not git("diff", source_commit, "HEAD", "--", "framework", "examples"), "Application/probe sources changed since native producer")
    require(not git("diff", "HEAD", "--", "framework", "examples"), "Uncommitted application/probe changes")
    clean = not git("status", "--porcelain")
    require(clean or args.control, "Full staging bundle requires a clean runner pin")
    require(digest(args.bun_linux) == BUN_SHA256, "Linux Bun 1.3.10 release binary hash mismatch")
    runtime = json.loads(args.runtime_pin.read_text())
    require(runtime and all(Path(name).is_absolute() for name in runtime), "Runtime pin missing")
    root = args.output.resolve()
    root.mkdir(mode=0o700)
    for app in ("switchyard", "rooms", "agent"):
        (root / app).mkdir()
        for name in ("app", "libe_sqlite3.so"):
            relative = f"{app}/{name}"
            source = (args.native / relative).resolve()
            require(source.is_relative_to(args.native.resolve()) and digest(source) == native["sha256"][relative], "Native artifact hash mismatch")
            shutil.copy2(source, root / relative)
        (root / app / "app").chmod(0o755)
    shutil.copy2(args.bun_linux, root / "bun")
    (root / "bun").chmod(0o755)
    shutil.copy2(REPO / "scripts/test-operational-soak.py", root / "operational.py")
    shutil.copy2(REPO / "scripts/test-staged-native.py", root / "stage-runner.py")
    (root / "probes").mkdir()
    for source, target in (("examples/switchyard/acceptance.test.ts", "acceptance.test.js"), ("examples/realtime-rooms/smoke.ts", "rooms.js")):
        subprocess.run([shutil.which("bun"), "build", source, "--target=bun", "--outfile", str(root / "probes" / target)], cwd=REPO, check=True, timeout=45)
    checksums = {str(path.relative_to(root)): digest(path) for path in sorted(root.rglob("*")) if path.is_file()}
    require(sum(path.stat().st_size for path in root.rglob("*") if path.is_file()) <= 256 * 1024 * 1024, "Bundle exceeds transfer/storage admission budget")
    manifest = {"protocol": "reptar-native-staging-v1", "sourceCommit": source_commit, "sourceTree": native["sourceTree"], "runnerSourceCommit": git("rev-parse", "HEAD"), "runnerClean": clean, "nativeProducer": native, "bunVersion": "1.3.10", "sha256": checksums, "runtimeSha256": runtime, "limits": ["Not a deployment or elapsed result", "Recorded host runtime hashes are rechecked, not an immutable OCI image claim", "Short controls never earn 24-hour evidence"]}
    (root / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
    archive = args.output.with_suffix(".tar")
    with tarfile.open(archive, "w") as stream:
        for path in sorted(root.rglob("*")):
            stream.add(path, arcname=str(path.relative_to(root)), recursive=False)
    print(json.dumps({"bundle": str(root), "archive": str(archive), "sha256": digest(archive), "bytes": archive.stat().st_size, "runnerClean": clean}, indent=2))


if __name__ == "__main__":
    main()
