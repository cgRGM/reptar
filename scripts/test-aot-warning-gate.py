#!/usr/bin/env python3
import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location("aot_warnings", Path(__file__).with_name("check-aot-warnings.py"))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class WarningGateTests(unittest.TestCase):
    def test_checkout_paths_normalize_but_source_spans_are_preserved(self):
        first = module.diagnostics("/private/tmp/owned/framework/Reptar/File.cs(3,7): warning IL2026: reviewed [/private/tmp/owned/framework/Reptar/Reptar.csproj]")
        second = module.diagnostics("/home/runner/work/reptar/framework/Reptar/File.cs(3,7): warning IL2026: reviewed [/home/runner/work/reptar/framework/Reptar/Reptar.csproj]")
        self.assertEqual(first, second)
        self.assertIn("framework/Reptar/File.cs(3,7)|IL2026|reviewed", first)

    def test_known_instance_passes(self):
        diagnostic = "ILC|IL2026|Reptar.Member(): reviewed"
        result = module.verify({diagnostic}, {"schema": 1, "instances": [{"diagnostic": diagnostic, "review": "Exact reviewed instance, not a code-wide exemption"}]})
        self.assertEqual(1, result["observedInstances"])

    def test_new_callsite_cannot_hide_behind_same_warning_code(self):
        with self.assertRaisesRegex(ValueError, "Unreviewed"):
            module.verify({"new.cs(2,3)|IL2026|same diagnostic"}, {"schema": 1, "instances": [{"diagnostic": "old.cs(2,3)|IL2026|same diagnostic", "review": "known"}]})

    def test_native_always_throw_is_fatal_even_if_previously_recorded(self):
        with self.assertRaisesRegex(ValueError, "Fatal"):
            module.diagnostics("ILC : warning IL9999: Method will always throw because an assembly is missing")

    def test_unexpanded_assembly_warning_is_not_an_approved_instance(self):
        with self.assertRaisesRegex(ValueError, "Fatal"):
            module.diagnostics("ILC : warning IL2104: Assembly produced trim warnings")

    def test_unknown_warning_format_fails_closed(self):
        with self.assertRaisesRegex(ValueError, "Unrecognized"):
            module.diagnostics("native compiler: warning: unexpected platform problem")

    def test_missing_review_is_rejected(self):
        with self.assertRaisesRegex(ValueError, "explicit review"):
            module.verify(set(), {"schema": 1, "instances": [{"diagnostic": "some.cs|IL2026|message"}]})

    def test_build_summary_is_not_a_diagnostic(self):
        self.assertEqual(set(), module.diagnostics("  12 Warning(s)\nBuild succeeded."))


if __name__ == "__main__":
    unittest.main()
