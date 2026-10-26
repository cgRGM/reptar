#!/usr/bin/env python3
"""Regression tests for the CI security-report gate; standard library only."""

import contextlib
import importlib.util
import io
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location("check_semgrep", Path(__file__).with_name("check-semgrep.py"))
assert spec is not None and spec.loader is not None
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


def finding(severity):
    return {
        "check_id": "regression-probe",
        "path": "examples/test/app.cs",
        "start": {"line": 7},
        "extra": {"severity": severity, "message": "A security finding"},
    }


class SecurityGateTests(unittest.TestCase):
    def check(self, report):
        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            code = module.check_report(report)
        return code, output.getvalue()

    def test_findings_fail_except_info(self):
        for severity in ("ERROR", "WARNING", "UNKNOWN"):
            with self.subTest(severity=severity):
                code, output = self.check({"results": [finding(severity)]})
                self.assertEqual(code, 1)
                self.assertIn("::error file=examples/test/app.cs,line=7::", output)
        code, output = self.check({"results": [finding("INFO")]})
        self.assertEqual(code, 0)
        self.assertIn("::notice", output)

    def test_reviewed_parser_gap_is_visible_not_silent(self):
        code, output = self.check({"results": [], "errors": [{
            "type": ["PartialParsing", []], "level": "warn",
            "path": "examples/switchyard/app.cs", "message": "Unsupported syntax",
        }]})
        self.assertEqual(code, 0)
        self.assertIn("::warning", output)
        self.assertIn("coverage is partial", output)

    def test_unknown_parser_gap_and_other_errors_fail_closed(self):
        for kind, path in (("PartialParsing", "examples/new/app.cs"), ("InvalidRule", "examples/switchyard/app.cs")):
            with self.subTest(kind=kind):
                code, output = self.check({"results": [], "errors": [{
                    "type": kind, "level": "warn", "path": path, "message": "Scan incomplete",
                }]})
                self.assertEqual(code, 1)
                self.assertIn("::error", output)

    def test_annotations_escape_control_characters(self):
        probe = finding("WARNING")
        probe["path"] = "examples/bad,name.cs"
        probe["extra"]["message"] = "line one\n::notice::injected\r100%"
        code, output = self.check({"results": [probe]})
        self.assertEqual(code, 1)
        self.assertEqual(len(output.splitlines()), 1)
        self.assertIn("bad%2Cname.cs", output)
        self.assertIn("%0A::notice::injected%0D100%25", output)


if __name__ == "__main__":
    unittest.main()
