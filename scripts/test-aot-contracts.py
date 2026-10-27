#!/usr/bin/env python3
"""Exercise real generated mapping and fail-closed Native AOT boundaries."""
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile


def main():
    binary = Path(sys.argv[1]).resolve()
    failures = []
    with tempfile.TemporaryDirectory(prefix="reptar-aot-contracts-") as directory:
        database = Path(directory) / "contracts.db"
        for mode in ["seed", "cold", "bad-row", "bad-parameter", "bad-insert", "bad-update", "bad-json"]:
            result = subprocess.run([str(binary), mode, str(database)], cwd=directory, capture_output=True, text=True, timeout=15)
            print(result.stdout, end="")
            if result.returncode != 0 or result.stdout.strip() != f"PASS {mode}":
                failures.append(mode)
                print(f"FAIL {mode} (exit {result.returncode})\n{result.stderr}", file=sys.stderr)
        if failures:
            raise AssertionError(f"Native contract failures: {', '.join(failures)}")
    print(json.dumps({"result": "passed", "binarySha256": hashlib.sha256(binary.read_bytes()).hexdigest(), "checks": ["cold-generated-mapper", "reordered-columns", "generated-json-roundtrip", "scalar-projection", "unmapped-row-rejected", "anonymous-parameter-rejected-before-write", "anonymous-table-writes-rejected-before-mutation", "missing-json-metadata-rejected"]}, indent=2))


if __name__ == "__main__":
    main()
