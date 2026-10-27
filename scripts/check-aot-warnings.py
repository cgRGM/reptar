#!/usr/bin/env python3
"""Reject unreviewed native-publish diagnostics without suppressing build output."""
import argparse
import json
from pathlib import Path
import re
import sys

WARNING = re.compile(r"\bwarning ([A-Z]+\d+):\s*(.*)")
FATAL = re.compile(r"will always throw|IL2104|IL3053", re.IGNORECASE)
ROOT = re.compile(r"^.*?((?:framework|examples|scripts)/)")


def diagnostics(text):
    result = set()
    for line in text.splitlines():
        if FATAL.search(line):
            raise ValueError(f"Fatal/unexpanded artifact diagnostic: {line}")
        match = WARNING.search(line)
        if not match:
            if re.search(r"\bwarning\b", line, re.IGNORECASE) and not re.match(r"^\s*\d+\s+Warning\(s\)", line):
                raise ValueError(f"Unrecognized warning format: {line}")
            continue
        location = line[:match.start()].strip().replace("\\", "/")
        location = ROOT.sub(r"\1", location)
        location = re.sub(r"\s*:\s*(?:Trim analysis|AOT analysis)?\s*$", "", location)
        message = re.sub(r"\s+\[[^\]]+\.(?:csproj|proj)\]$", "", match.group(2))
        result.add(f"{location}|{match.group(1)}|{message}")
    return result


def verify(observed, baseline):
    if baseline.get("schema") != 1:
        raise ValueError("Unknown AOT diagnostic baseline schema")
    approved = set()
    for instance in baseline["instances"]:
        if not instance.get("review") or not instance.get("diagnostic"):
            raise ValueError("Every diagnostic instance requires an explicit review")
        if instance["diagnostic"] in approved:
            raise ValueError("Duplicate diagnostic baseline instance")
        approved.add(instance["diagnostic"])
    unknown = sorted(observed - approved)
    if unknown:
        raise ValueError("Unreviewed native-publish diagnostics:\n" + "\n".join(unknown))
    return {"observedInstances": len(observed), "knownNotObserved": len(approved - observed)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("logs", type=Path, nargs="+")
    parser.add_argument("--baseline", type=Path, default=Path(__file__).parent / "testing/aot-warning-baseline.json")
    arguments = parser.parse_args()
    observed = set()
    for log in arguments.logs:
        text = log.read_text()
        if not text.strip():
            raise ValueError(f"Empty publish log: {log}")
        observed.update(diagnostics(text))
    result = verify(observed, json.loads(arguments.baseline.read_text()))
    print(json.dumps({"result": "passed", **result, "logs": len(arguments.logs)}, indent=2))


if __name__ == "__main__":
    try:
        main()
    except ValueError as error:
        print(error, file=sys.stderr)
        raise SystemExit(1) from error
