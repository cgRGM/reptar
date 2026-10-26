#!/usr/bin/env python3
"""Gate findings, retain reviewed C# parser warnings, reject new scan gaps."""

import json
import sys

# Semgrep 1.182.1 lacks .NET 10 directives, some primary constructors and
# unexpanded template identifiers. CodeQL compiles these applications;
# scaffold E2E compiles the rendered template. Do not silently add new gaps.
KNOWN_SYNTAX_GAPS = frozenset({
    "examples/agent-cli/app.cs",
    "examples/agent-cli/terminal/TerminalAgent.cs",
    "examples/baas/app.cs",
    "examples/campfire/app.cs",
    "examples/switchyard/app.cs",
    "templates/fullstack/apps/api/features/items.cs",
})


def escape_data(value: object) -> str:
    return str(value).replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")


def annotate(level: str, message: object, path: str = "", line: int = 0) -> None:
    properties = ""
    if path:
        properties = " file=" + escape_data(path).replace(":", "%3A").replace(",", "%2C")
    if line:
        properties += f",line={int(line)}"
    print(f"::{level}{properties}::{escape_data(message)}")


def check_report(report: dict) -> int:
    failed = False
    for error in report.get("errors", []):
        kind = error.get("type", "")
        if isinstance(kind, list):
            kind = kind[0]
        path = error.get("path", "")
        known_gap = (
            kind == "PartialParsing"
            and error.get("level") == "warn"
            and path in KNOWN_SYNTAX_GAPS
        )
        if known_gap:
            annotate("warning", f"Semgrep parser coverage is partial: {error.get('message', '')}", path)
        else:
            failed = True
            annotate("error", error.get("message", "Unknown scanner error"), path)

    for finding in report["results"]:
        severity = finding["extra"]["severity"]
        advisory = severity == "INFO"
        if not advisory:
            failed = True
        annotate(
            "notice" if advisory else "error",
            f"{finding['check_id']}: {finding['extra']['message']}",
            finding["path"],
            finding["start"]["line"],
        )
    return int(failed)


if __name__ == "__main__":
    with open(sys.argv[1], encoding="utf-8") as source:
        raise SystemExit(check_report(json.load(source)))
