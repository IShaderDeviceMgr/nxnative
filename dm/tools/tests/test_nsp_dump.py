"""NSP script: a dump folder (a game a dumper wrote as plain files: '<name> [<title ID>]/base/{exefs,romfs}', 'updates/<version>/{exefs,romfs}') is surveyed without keys,
its title ID taken from main.npdm and the folder name (a Switch 2 title's starts with 04), and a Switch 2 game (main.npdm's extended ACID) stops right after the survey.
Synthetic files only: no game files, keys or Xcode needed (temporary files only)."""
import importlib.util
import os
from pathlib import Path
import shutil
import struct
import sys
import tempfile
import unittest
from unittest import mock

REPO = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(REPO / 'dm/games')); sys.path.insert(0, str(REPO / 'dm/tools'))
import profile_guess
spec = importlib.util.spec_from_file_location('build_ipa_from_nsp', REPO / 'scripts/build_ipa_from_nsp.py')
if spec is None or spec.loader is None: raise ImportError('scripts/build_ipa_from_nsp.py')
nsp = importlib.util.module_from_spec(spec); spec.loader.exec_module(nsp)
vars(nsp)['pg'] = profile_guess   # main sets it

APP = 0x0400AAAA00000000   # a Switch 2 title ID: 04…, not 01…


def npdm(program=APP, body=0x300, flags=0x27, svcs=(0x01, 0x80)):
    """A main.npdm: META, the ACID at 0x80 with its body ('ACID') at +body (Switch 0x200, Switch 2 0x300), then ACI0 with the program ID and a kernel capability allowing svcs"""
    acid = 0x80; aci = acid + body + 0x100
    kac = b''.join(struct.pack('<I', (i << 29) | (sum(1 << (5 + s - i * 24) for s in svcs if s // 24 == i) & 0x1FFFFFE0) | 0x0F) for i in sorted({s // 24 for s in svcs}))
    d = bytearray(aci + 0x40 + len(kac))
    d[:4] = b'META'; d[0xc] = flags
    struct.pack_into('<IIII', d, 0x70, aci, 0x40 + len(kac), acid, body + 0x100)
    d[acid + body:acid + body + 4] = b'ACID'
    d[aci:aci + 4] = b'ACI0'; struct.pack_into('<Q', d, aci + 0x10, program); struct.pack_into('<II', d, aci + 0x30, 0x40, len(kac))
    d[aci + 0x40:] = kac
    return bytes(d)


def version(d, program: int | None = APP, romfs=None, body=0x300):   # a version folder: exefs (main, main.npdm, macOS's .DS_Store) and an optional romfs of {path: bytes}
    (d / 'exefs').mkdir(parents=True)
    (d / 'exefs/main').write_bytes(b'NSO0' + bytes(0x3c) + bytes(range(16)) + bytes(0x50))
    if program is not None: (d / 'exefs/main.npdm').write_bytes(npdm(program, body))
    (d / 'exefs/.DS_Store').write_bytes(b'x')
    for p, b in (romfs or {}).items():
        f = d / 'romfs' / p; f.parent.mkdir(parents=True, exist_ok=True); f.write_bytes(b)


class Npdm(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp()); self.addCleanup(shutil.rmtree, self.tmp)

    def info(self, data):
        p = self.tmp / 'main.npdm'; p.write_bytes(data); i = profile_guess.npdm_info(p)
        assert i is not None
        return i

    def test_switch2_by_the_extended_acid(self):
        i = self.info(npdm())
        self.assertEqual((i['console'], i['acid'], i['flags']), ('Switch 2', 0x80, 0x27))
        self.assertEqual(i['svcs'], {0x01, 0x80})
        self.assertEqual(profile_guess.npdm(self.tmp / 'main.npdm')[1], APP)   # the program ID as before: ACI0 is found by its magic

    def test_switch(self):
        i = self.info(npdm(0x0100AAAA00000000, body=0x200, flags=0x3f, svcs=(0x01, 0x7f)))
        self.assertEqual((i['console'], i['svcs']), ('Switch', {0x01, 0x7f}))

    def test_unknown_and_missing(self):
        self.assertIsNone(self.info(npdm(body=0x280))['console'])
        self.assertIsNone(self.info(b'META' + bytes(0x7c))['console'])
        self.assertIsNone(profile_guess.npdm_info(self.tmp / 'none.npdm'))

    def test_switch2_of_an_exefs(self):
        (self.tmp / 'main.npdm').write_bytes(npdm()); self.assertTrue(profile_guess.switch2(self.tmp))
        (self.tmp / 'main.npdm').write_bytes(npdm(body=0x200)); self.assertFalse(profile_guess.switch2(self.tmp))


class Dump(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp()); self.addCleanup(shutil.rmtree, self.tmp)
        self.dump = self.tmp / f'Some Game [{APP:016X}]'   # the brackets are glob patterns: nothing may glob this path
        self.said = []
        for cm in (mock.patch.object(nsp, 'say', self.said.append), mock.patch.object(nsp, 'PROG', mock.Mock())):
            cm.__enter__(); self.addCleanup(cm.__exit__, None, None, None)

    def red(self): return [l for l in self.said if l.startswith('[RED] ')]

    def test_scan(self):
        version(self.dump / 'base', romfs={'a.bin': b'A'})
        version(self.dump / 'updates/327680', romfs={'a.bin': b'B'}); version(self.dump / 'updates/65536')
        (self.dump / 'updates/notes').mkdir(); (self.dump / 'dlc').mkdir(); (self.dump / '.DS_Store').write_bytes(b'x')
        (self.dump / f'{APP:016X}-work').mkdir(); (self.dump / f'{APP:016X}_survey').mkdir()   # the work folders beside the dump are known
        self.assertTrue(nsp.is_dump(self.dump))
        app, fam = nsp.scan_dump(self.dump)
        self.assertEqual(app, APP)
        self.assertEqual([v for v, _ in fam['updates']], [65536, 327680])
        self.assertEqual((fam['name'], fam['dump'], fam['dlc']), ('Some Game', True, []))
        self.assertEqual(fam['base']['romfs'], self.dump / 'base/romfs')
        self.assertIsNone(fam['updates'][0][1]['romfs'])
        red = self.red()
        self.assertEqual(len(red), 2, red)
        self.assertTrue(any('notes' in l and 'not an update version folder' in l for l in red), red)
        self.assertTrue(any(l.endswith('dlc: DLC in a dump is not read yet (its layout is not known), left out') for l in red), red)

    def test_not_a_dump(self):
        self.dump.mkdir(); (self.dump / 'base').mkdir(); (self.dump / f'Some Game [{APP:016X}].nsp').write_bytes(b'PFS0')
        self.assertFalse(nsp.is_dump(self.dump))
        self.assertFalse(nsp.is_dump(self.tmp))

    def test_title_id_from_the_name_without_an_npdm(self):
        version(self.dump / 'base', program=None)
        self.assertEqual(nsp.scan_dump(self.dump)[0], APP)
        bare = self.tmp / 'Some Game'; version(bare / 'base', program=None)
        with self.assertRaises(nsp.BuildError): nsp.scan_dump(bare)

    def test_title_ids_must_agree(self):
        version(self.dump / 'base', program=APP + 0x1000)
        with self.assertRaises(nsp.BuildError) as e: nsp.scan_dump(self.dump)
        self.assertIn(f'{APP + 0x1000:016X}', str(e.exception))

    def test_no_base(self):
        version(self.dump / 'updates/65536')
        with self.assertRaises(nsp.BuildError): nsp.scan_dump(self.dump)

    def test_extraction_takes_the_update(self):
        version(self.dump / 'base', romfs={'a.bin': b'A' * 40, 'gone.bin': b'G'})
        version(self.dump / 'updates/65536', romfs={'a.bin': b'B' * 40, 'sub/c.bin': b'C', 'sub/.DS_Store': b'x', '._a.bin': b'x', 'sub/.listing': b'x'})
        _, fam = nsp.scan_dump(self.dump)
        out = self.tmp / 'extract'; lines = nsp.dump_extraction(fam, fam['updates'][-1][1], out, self.tmp / 'logs/extract.log')
        self.assertEqual(sorted(p.name for p in (out / 'ExeFS').iterdir()), ['main', 'main.npdm'])
        self.assertEqual((out / 'ExeFS/main').resolve(), (self.dump / 'updates/65536/exefs/main').resolve())
        rows = list(profile_guess.romfs_files(out / 'romfs_manifest.tsv'))   # read back as the survey reads nx_extract's manifest; the dumper's .listing is no game file
        self.assertEqual(rows, [('/a.bin', 40, b'B' * 32), ('/sub/c.bin', 1, b'C')])
        # one of two base files gone: a file the update replaced, not a patch
        self.assertTrue(any(l.startswith('dump: 1 of the base game\'s 2 RomFS files') and '/gone.bin' in l for l in lines), lines)
        self.assertNotIn('[RED]', (self.tmp / 'logs/extract.log').read_text())

    def test_extraction_update_romfs_looks_like_a_patch(self):
        version(self.dump / 'base', romfs={'a.bin': b'A', 'b.bin': b'B', 'c.bin': b'C'})
        version(self.dump / 'updates/65536', romfs={'a.bin': b'A2'})
        _, fam = nsp.scan_dump(self.dump)
        lines = nsp.dump_extraction(fam, fam['updates'][-1][1], self.tmp / 'extract', self.tmp / 'logs/extract.log')
        self.assertTrue(any(l.startswith('[RED] 2 of the base game\'s 3 RomFS files') for l in lines), lines)

    def test_listing_files_are_not_parts_of_the_dump(self):
        version(self.dump / 'base'); version(self.dump / 'updates/65536')
        for d in ('', 'base', 'updates', 'updates/65536'): (self.dump / d / '.listing').write_text('dr-xr-xr-x 1 ftp ftp 0 Jan 01  1970 exefs\n')
        with mock.patch.object(nsp, 'say') as say: nsp.scan_dump(self.dump)
        self.assertFalse([c for c in say.call_args_list if '[RED]' in str(c)], say.call_args_list)

    def test_extraction_without_a_romfs(self):
        version(self.dump / 'base')
        _, fam = nsp.scan_dump(self.dump)
        out = self.tmp / 'extract'; nsp.dump_extraction(fam, None, out, self.tmp / 'extract.log')
        self.assertEqual(list(profile_guess.romfs_files(out / 'romfs_manifest.tsv')), [])

    def test_stop_after_survey(self):
        ex = self.tmp / 'ExeFS'; ex.mkdir()
        (ex / 'main.npdm').write_bytes(npdm())
        with self.assertRaises(nsp.BuildError) as e: nsp.stop_after_survey({'dump': True}, ex, 'survey.md')
        self.assertIn('Switch 2 games can only be surveyed so far', str(e.exception))
        (ex / 'main.npdm').write_bytes(npdm(0x0100AAAA00000000, body=0x200))
        with self.assertRaises(nsp.BuildError) as e: nsp.stop_after_survey({'dump': True}, ex, 'survey.md')
        self.assertIn('a dump folder (already extracted files) can only be surveyed so far', str(e.exception))
        nsp.stop_after_survey({'base': None}, ex, 'survey.md')   # a Switch game from NSPs goes on

    def test_main_needs_no_keys_and_stops_after_the_survey(self):
        version(self.dump / 'base')
        args = nsp.parse_args([str(self.dump), '--progress', 'off'])
        surveyed = []
        def survey(nsp_dir, app, fam, keys, repo, py, env):   # the survey's ExeFS, as dump_extraction lays it out
            surveyed.append((app, keys)); ex = nsp_dir / f'{app:016X}_survey/extract/ExeFS'; ex.mkdir(parents=True, exist_ok=True)
            (ex / 'main.npdm').write_bytes(npdm()); return {}
        with mock.patch.object(nsp, 'survey', survey), mock.patch.object(nsp, 'ask', mock.Mock(side_effect=AssertionError('asked'))), \
             mock.patch.object(nsp, 'check_native_python', lambda: None), mock.patch.object(nsp, '_importable', lambda m: True), \
             mock.patch.dict(os.environ, {'DM_GAMES_DIR': str(self.tmp / 'no-games')}):
            with self.assertRaises(nsp.BuildError) as e: nsp.main(args)
            self.assertIn('Switch 2 games can only be surveyed so far', str(e.exception))
            self.assertEqual(surveyed, [(APP, None)])
            self.assertFalse((self.dump / f'{APP:016X}_build/profile').exists())   # stopped before a provisional profile
            nsp.main(nsp.parse_args([str(self.dump), '--progress', 'off', '--survey-only']))   # --survey-only: done after the survey, no error
        with mock.patch.object(nsp, 'ask', mock.Mock(side_effect=AssertionError('asked'))), mock.patch.object(nsp, 'check_native_python', lambda: None):
            with self.assertRaises(nsp.BuildError) as e: nsp.main(nsp.parse_args([str(self.tmp), '--progress', 'off']))   # the folder of dump folders
            self.assertIn(f'holds dump folders: give one game\'s folder, one of {self.dump.name}', str(e.exception))


if __name__ == '__main__': unittest.main()
