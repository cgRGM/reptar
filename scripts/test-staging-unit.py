#!/usr/bin/env python3
"""Pure ownership, sandbox-command and archive negative controls; creates no remote resources."""
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import tarfile
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('unit', Path(__file__).with_name('staging-unit.py'))
unit = importlib.util.module_from_spec(spec)
spec.loader.exec_module(unit)


def request():
    return {'action': 'read', 'runId': 'reptar-reliability-20261028-012345abcdef', 'ownerId': 'a' * 32, 'archiveSha256': 'b' * 64, 'helperSha256': 'd' * 64, 'seconds': 12, 'sourceCommit': 'c' * 40}


class UnitTests(unittest.TestCase):
    def test_invalid_name_cannot_reach_command(self):
        value = request()
        value['runId'] = 'campfire; rm -rf /'
        with self.assertRaisesRegex(AssertionError, 'Invalid owned run name'):
            unit.validate(value)

    def test_unknown_action_and_duration_refused(self):
        for key, value in (('action', 'restart-all'), ('seconds', 86401), ('ownerId', ''), ('sourceCommit', 'main')):
            item = request()
            item[key] = value
            with self.subTest(key=key), self.assertRaises(AssertionError):
                unit.validate(item)

    def test_hard_caps_and_expiry_in_actual_command(self):
        value = request()
        value['seconds'] = 86400
        command = unit.start_command(value, Path('/home/ubuntu/reptar-reliability') / value['runId'])
        self.assertEqual(command[:3], ['sudo', '-n', 'systemd-run'])
        for flag in ('--uid=1000', '--gid=1000', '--property=CPUQuota=100%', '--property=MemoryMax=512M', '--property=MemorySwapMax=0', '--property=TasksMax=128', '--property=PrivateNetwork=yes', '--property=NoNewPrivileges=yes', '--property=CapabilityBoundingSet=', '--property=ProtectSystem=strict', '--property=Restart=no', '--property=KillMode=control-group', '--property=RuntimeMaxSec=86700s', '--property=OOMPolicy=stop'):
            self.assertIn(flag, command)
        self.assertNotIn('bash', command)
        self.assertNotIn('sh', command)

    def test_foreign_directory_never_adopted(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            owner = unit.identity(request())
            owner['ownerId'] = '0' * 32
            (root / 'owner.json').write_text(json.dumps(owner))
            with patch.object(unit, 'inspect_unit', return_value={'LoadState': 'not-found'}), self.assertRaisesRegex(AssertionError, 'different resource'):
                unit.inspect(request(), root)

    def test_foreign_unit_never_adopted(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / 'owner.json').write_text(json.dumps(unit.identity(request())))
            with patch.object(unit, 'inspect_unit', return_value={'LoadState': 'loaded', 'Description': 'unrelated service'}), self.assertRaisesRegex(AssertionError, 'Unit ownership mismatch'):
                unit.inspect(request(), root)

    def test_archive_escape_and_link_refused(self):
        for kind in ('traversal', 'symlink', 'duplicate'):
            with self.subTest(kind=kind), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                with tarfile.open(root / 'payload.tar', 'w') as stream:
                    member = tarfile.TarInfo('../escape' if kind == 'traversal' else 'item')
                    if kind == 'symlink':
                        member.type = tarfile.SYMTYPE
                        member.linkname = '/etc/hosts'
                        stream.addfile(member)
                    else:
                        member.size = 1
                        stream.addfile(member, io.BytesIO(b'x'))
                        if kind == 'duplicate':
                            stream.addfile(member, io.BytesIO(b'y'))
                value = request()
                value['archiveSha256'] = hashlib.sha256((root / 'payload.tar').read_bytes()).hexdigest()
                with self.assertRaises(AssertionError):
                    unit.extract(value, root)
                self.assertFalse((root.parent / 'escape').exists())


if __name__ == '__main__':
    unittest.main()
