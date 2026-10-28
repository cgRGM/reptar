#!/usr/bin/env python3
"""Pure bundle-integrity controls; not native runtime or elapsed proving."""
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("stage", Path(__file__).with_name("test-staged-native.py"))
stage = importlib.util.module_from_spec(spec)
spec.loader.exec_module(stage)


class BundleTests(unittest.TestCase):
    def make_bundle(self, root):
        for name in stage.REQUIRED_FILES:
            path = root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(f"fixture:{name}")
        checksums = {name: stage.digest(root / name) for name in stage.REQUIRED_FILES}
        manifest = {"protocol": stage.PROTOCOL, "sha256": checksums, "sourceTree": "test-tree", "nativeProducer": {"sourceTree": "test-tree", "sha256": {name: value for name, value in checksums.items() if name.split('/')[0] in ('switchyard', 'rooms', 'agent')}}}
        (root / "manifest.json").write_text(json.dumps(manifest))
        return manifest

    def test_complete_inventory_and_hashes(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            manifest = self.make_bundle(root)
            self.assertEqual(stage.verify_bundle(root), manifest)

    def test_changed_payload_refused(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.make_bundle(root)
            (root / 'rooms/app').write_text('changed')
            with self.assertRaisesRegex(AssertionError, 'integrity failed'):
                stage.verify_bundle(root)

    def test_unexpected_secret_file_refused(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.make_bundle(root)
            (root / 'production.env').write_text('synthetic forbidden file')
            with self.assertRaisesRegex(AssertionError, 'Unexpected bundle contents'):
                stage.verify_bundle(root)

    def test_symlink_payload_refused(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.make_bundle(root)
            (root / 'bun').unlink()
            (root / 'bun').symlink_to('/etc/hosts')
            with self.assertRaisesRegex(AssertionError, 'symlink refused'):
                stage.verify_bundle(root)

    def test_producer_mismatch_refused_even_with_valid_payload_hash(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            manifest = self.make_bundle(root)
            manifest['nativeProducer']['sha256']['agent/app'] = '0' * 64
            (root / 'manifest.json').write_text(json.dumps(manifest))
            with self.assertRaisesRegex(AssertionError, 'producer hash mismatch'):
                stage.verify_bundle(root)

    def test_traversal_inventory_refused(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            manifest = self.make_bundle(root)
            manifest['sha256']['../outside'] = '0' * 64
            (root / 'manifest.json').write_text(json.dumps(manifest))
            with self.assertRaisesRegex(AssertionError, 'inventory mismatch'):
                stage.verify_bundle(root)


if __name__ == '__main__':
    unittest.main()
