#!/usr/bin/env python3
"""Actual-artifact short success, budget refusal and owner-stop controls (not elapsed proving)."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time


def require(condition, message):
    if not condition:
        raise AssertionError(message)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("switchyard", "rooms", "agent", "bun"):
        parser.add_argument(f"--{name}", required=True)
    args = parser.parse_args()
    evidence = Path(tempfile.mkdtemp(prefix="reptar-reliability-harness-controls-"))
    os.chmod(evidence, 0o700)
    runner = Path(__file__).with_name("test-operational-soak.py")
    common = [sys.executable, str(runner)]
    for name in ("switchyard", "rooms", "agent", "bun"):
        common.extend([f"--{name}", str(Path(getattr(args, name)).resolve())])
    results = {}
    for label, extra in (
        ("short", ["--seconds", "6", "--cadence", "3"]),
        ("rss-refusal", ["--seconds", "6", "--cadence", "3", "--max-native-rss-kib", "1"]),
        ("owner-term", ["--seconds", "120", "--cadence", "60"]),
    ):
        log = evidence / f"{label}.log"
        with log.open("w") as output:
            manager = subprocess.Popen(common + extra, stdout=output, stderr=output, start_new_session=True)
            try:
                deadline = time.monotonic() + 60
                root = None
                while time.monotonic() < deadline:
                    text = log.read_text()
                    if text.startswith("Owned operational evidence: ") and "\n" in text:
                        root = Path(text.splitlines()[0].split(": ", 1)[1])
                        break
                    require(manager.poll() is None, "Owner exited before manifest")
                    time.sleep(.025)
                require(root is not None, "No owned evidence path")
                if label == "owner-term":
                    while not (root / "observations.jsonl").exists():
                        require(manager.poll() is None and time.monotonic() < deadline, "Owner failed before interrupt control")
                        time.sleep(.025)
                    manager.terminate()
                code = manager.wait(timeout=90)
                result = json.loads((root / "result.json").read_text())
                require(result["ownedProcessesCleaned"] and not result["earnedTwoHourElapsed"], "Short/failed control claimed elapsed evidence or leaked processes")
                if label == "short":
                    require(code == 0 and result["status"] == "passed" and result["scope"] == "short-harness-validation-only", "Short functional control failed")
                else:
                    expected = "Native RSS exceeded observation budget" if label == "rss-refusal" else "InterruptedError"
                    require(code != 0 and result["status"] == "failed" and expected in result["error"], "Negative control failed for an unexpected reason")
                require(all(process["exited"] is not None for process in json.loads((root / "owned-processes.json").read_text())), "Owner left an unreaped process")
                results[label] = {"root": str(root), "log": str(log), "status": result["status"], "cleaned": True, "earnedTwoHourElapsed": False}
            finally:
                if manager.poll() is None:
                    manager.terminate()
                    manager.wait(timeout=30)
    (evidence / "controls.json").write_text(json.dumps(results, indent=2))
    print(json.dumps({"controlsEvidence": str(evidence), "controls": results}, indent=2))


if __name__ == "__main__":
    main()
