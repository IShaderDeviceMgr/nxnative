"""NVN 2's (Switch 2: SM8x code; found on Donkey Kong Bananza) BNSH, agl BAHS (version 11) and agl NDSR pack, read by shader_split's bnsh_shaders, bahs_shaders / bahs_sm8x and
ndsr_shaders. Synthetic containers built here pin down the layouts the readers take: BNSH and BAHS keep their Maxwell layouts with SM8x stages (the stage descriptor's or the entry's
control section decides the check), the BAHS control sections have the GLSLC output fields zeroed (sm8x_ctl takes them by +0xc4), NDSR records pair a control section with its code
area by kind. Every SM8x shader is labelled SM8X_TAG; BNSH variations and BAHS program variations still pair; damaged containers end in a '[RED] ' line or give nothing, never in
another exception; Maxwell BNSH and BAHS read exactly as before. No game files needed."""
import hashlib
import os
import random
import struct
import sys
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(REPO / 'dm/gfx/tools')); sys.path.insert(0, str(Path(__file__).resolve().parent))
_had = os.environ.get('DM_GAME'); os.environ['DM_GAME'] = '01007BB017812000'   # shader_split loads a profile at import; other tests expect no default game
import shader_split as ss
from test_glslc_sm8x import sm8x_stage   # (control section, code area) of one SM8x stage, as NVN 2's GLSLC writes it
if _had is None: del os.environ['DM_GAME']
else: os.environ['DM_GAME'] = _had

BNSH_SLOT = {'vs': 0, 'hs': 1, 'ds': 2, 'gs': 3, 'ps': 4, 'cs': 5}


def agl(stage):
    """A stage as agl's BAHS version 11 stores it: the control section's GLSLC output fields zeroed (+0x18 .. +0x33: compiler version, hash, its own length; +0x98, +0xbc, +0xc8),
    +0xc4 its length"""
    ctl, code = stage
    c = bytearray(ctl); c[0x18:0x34] = bytes(0x1c)
    for o in (0x98, 0xbc, 0xc8): struct.pack_into('<I', c, o, 0)
    struct.pack_into('<I', c, 0xc4, len(c))
    return bytes(c), code


def maxwell(sph_type):
    """A Maxwell stage: a 0x880-byte control section (+0x20 its length, +0x704 the code area's), a code area of a 0x30 header and an SPH (stage in word 0 bits 10..13)"""
    code = bytearray(ss.GFX_MAGIC + bytes(0x2c)); code += struct.pack('<I', sph_type) + bytes(0x4c) + bytes.fromhex('0f00000000000000') * 8
    ctl = bytearray(0x880); ctl[:4] = ss.CTL_MAGIC; struct.pack_into('<I', ctl, 0x20, 0x880); struct.pack_into('<I', ctl, 0x704, len(code))
    return bytes(ctl), bytes(code)


def bnsh(variations, version=0x20400):
    """A BNSH: +0x08 version, +0x1c its length, grsc at +0x60, +0x7c the variation count, +0x80 the variation table (0x40 bytes each, +0x10 the program); a program's 6 stage pointers
    from +0x08 (vertex, hull, domain, geometry, pixel, compute); a stage descriptor's +0x08 control, +0x10 code area, +0x18 code length, +0x1c control length. A stage given twice (the
    same objects) is stored once, as the BNSH of NVN 2 share control sections and code areas between variations"""
    d = bytearray(0xc0); d[:8] = b'BNSH\0\0\0\0'; struct.pack_into('<I', d, 8, version); d[0x60:0x64] = b'grsc'
    struct.pack_into('<IQ', d, 0x7c, len(variations), 0xc0)
    d += bytes(0x40 * len(variations)); data = {}
    for v, stages in enumerate(variations):
        prog = len(d); d += bytes(0x40); struct.pack_into('<Q', d, 0xc0 + 0x40 * v + 0x10, prog)
        for st, (ctl, code) in stages.items():
            if id(ctl) not in data:
                d += bytes(-len(d) % 0x100); at = len(d); d += ctl; d += bytes(-len(d) % 0x100); k = len(d); d += code
                desc = len(d); d += bytes(0x20); struct.pack_into('<QQII', d, desc + 8, at, k, len(code), len(ctl)); data[id(ctl)] = desc
            struct.pack_into('<Q', d, prog + 8 + 8 * BNSH_SLOT[st], data[id(ctl)])
    struct.pack_into('<I', d, 0x1c, len(d))
    return bytes(d)


def bahs(programs):
    """An agl BAHS: header +0x18 code region length, +0x1c its start; a binary array (u32 byte size, u32 count, 0x20-byte records {size, kind 0 vertex / 1 fragment / 3 compute, 0, 0,
    the 16-byte entry {code offset, 0, control length, signed offset of the control from the entry}}), then a program array (u32 byte size, u32 count, records {size, name length,
    stage mask, first binary}); the control sections after the arrays, the code areas in the code region at 0x100-aligned offsets. programs: [[(kind, (control, code)), …], …]"""
    kinds = [b for p in programs for b in p]
    arr = 0x40; nrec = len(kinds); prog = arr + 8 + 0x20 * nrec
    ctls = prog + 8 + 16 * len(programs); ctls += -ctls % 0x10
    d = bytearray(ctls); d[:4] = b'BAHS'; struct.pack_into('<I', d, 4, 11)
    struct.pack_into('<II', d, arr, 8 + 0x20 * nrec, nrec)
    code = bytearray()
    for i, (kind, (ctl, k)) in enumerate(kinds):
        rec = arr + 8 + 0x20 * i; ent = rec + 0x10; c = len(d); d += ctl + bytes(-len(ctl) % 0x10)
        code += bytes(-len(code) % 0x100); off = len(code); code += k
        struct.pack_into('<II', d, rec, 0x20, kind); struct.pack_into('<IIIi', d, ent, off, 0, len(ctl), c - ent)
    struct.pack_into('<II', d, prog, 8 + 16 * len(programs), len(programs)); first = 0
    for p, binaries in enumerate(programs):
        struct.pack_into('<IIII', d, prog + 8 + 16 * p, 16, 0, 0x11, first); first += len(binaries)
    d += bytes(-len(d) % 0x100); region = len(d); code += bytes(-len(code) % 0x100)
    struct.pack_into('<II', d, 0x18, len(code), region)
    return bytes(d + code)


def ndsr(records):
    """An NDSR pack: 'NDSR', +0x04 0x300, +0x08 0xfeff, +0x0a the record count, 8-byte records {offset, length << 4 | kind} from +0x10. records: [(kind, bytes)], the bytes stored at
    0x100-aligned offsets after the records"""
    d = bytearray(0x10 + 8 * len(records)); d[:4] = b'NDSR'; struct.pack_into('<IHH', d, 4, 0x300, 0xfeff, len(records))
    for i, (kind, b) in enumerate(records):
        d += bytes(-len(d) % 0x100); struct.pack_into('<II', d, 0x10 + 8 * i, len(d), len(b) << 4 | kind); d += b
    return bytes(d)


def scan(d):
    """scan_blob as scan_file runs it on a file, with the BNSH / BAHS pairs; BAHS_VAR emptied after"""
    found = []
    try:
        ss.scan_blob(d, '/t', found); pairs = ss.var_pairs(found)
    finally: ss.BAHS_VAR.clear()
    return found, pairs


VS, PS, GS, CS = sm8x_stage('vs'), sm8x_stage('ps', ninstr=8, imap=0xff), sm8x_stage('gs'), sm8x_stage('cs')


class Bnsh(unittest.TestCase):
    def test_program(self):
        found, pairs = scan(bnsh([{'vs': VS, 'ps': PS}]))
        self.assertEqual(found, [('/t#BNSH@0x0#var0#SM8X', 'vs') + VS, ('/t#BNSH@0x0#var0#SM8X', 'ps') + PS])
        self.assertEqual(pairs, {(ss.skey(*VS), ss.skey(*PS))})
        self.assertEqual(ss.skey(*VS), hashlib.sha256(VS[0] + VS[1]).hexdigest()[:16])   # the whole control section is in the key

    def test_shared_stages_and_versions(self):
        """Variations that share stages (the material bfsha: 12,480 variations, 2,237 control sections), a geometry stage, version 2.3.1, a compute variation"""
        ps2 = sm8x_stage('ps', ninstr=24)
        for version in (0x20400, 0x20301):
            with self.subTest(version=hex(version)):
                found, pairs = scan(bnsh([{'vs': VS, 'ps': PS}, {'vs': VS, 'gs': GS, 'ps': ps2}, {'cs': CS}], version))
                self.assertEqual([(w, st) for w, st, _, _ in found], [('/t#BNSH@0x0#var0#SM8X', 'vs'), ('/t#BNSH@0x0#var0#SM8X', 'ps'), ('/t#BNSH@0x0#var1#SM8X', 'vs'),
                                                                     ('/t#BNSH@0x0#var1#SM8X', 'gs'), ('/t#BNSH@0x0#var1#SM8X', 'ps'), ('/t#BNSH@0x0#var2#SM8X', 'cs')])
                self.assertEqual(pairs, {(ss.skey(*VS), ss.skey(*PS)), (ss.skey(*VS), ss.skey(*ps2))})
                self.assertEqual(len(ss.uniq_keys(found)), 5)

    def test_stage_slot_must_match_the_control(self):
        """The slot is the stage: a control section of another stage (control +0x54) and SPH is a damaged BNSH, reported, never read as another stage"""
        for d in (bnsh([{'vs': PS, 'ps': PS}]), bnsh([{'cs': VS}])):
            with self.assertRaises(SystemExit) as e: scan(d)
            self.assertTrue(str(e.exception).startswith('[RED] /t BNSH @0x0 variation 0 '), str(e.exception))
            self.assertTrue('SPH' in str(e.exception) or 'thread group' in str(e.exception), str(e.exception))

    def test_damaged(self):
        good = bnsh([{'vs': VS, 'ps': PS}])
        desc = struct.unpack_from('<Q', good, struct.unpack_from('<Q', good, 0xc0 + 0x10)[0] + 8)[0]
        def patched(off, fmt, *v):
            d = bytearray(good); struct.pack_into(fmt, d, off, *v); return bytes(d)
        for why, d in {'code length': patched(desc + 0x18, '<I', len(VS[1]) - 0x80), 'control length': patched(desc + 0x1c, '<I', 0xd0),
                       'variation count': patched(0x7c, '<I', 0x1000), 'control past the end': patched(desc + 8, '<Q', len(good) - 0x10)}.items():
            with self.subTest(why=why):
                with self.assertRaises(SystemExit) as e: scan(d)
                self.assertTrue(str(e.exception).startswith('[RED] '), str(e.exception))

    def test_maxwell_unchanged(self):
        vs, ps = maxwell(0x0461), maxwell(0x1462)
        found, pairs = scan(bnsh([{'vs': vs, 'ps': ps}], 0x20108))
        self.assertEqual(found, [('/t#BNSH@0x0#var0', 'vs') + vs, ('/t#BNSH@0x0#var0', 'ps') + ps])
        self.assertEqual(pairs, {(ss.skey(*vs), ss.skey(*ps))})
        self.assertFalse(ss.sm8x_ctl(vs[0]))


class Bahs(unittest.TestCase):
    def test_programs(self):
        avs, aps, aps2, acs = agl(VS), agl(PS), agl(sm8x_stage('ps', ninstr=24)), agl(CS)
        found, pairs = scan(bahs([[(0, avs), (1, aps), (0, avs), (1, aps2)], [(3, acs)]]))
        self.assertEqual([(w, st) for w, st, _, _ in found], [(f'/t#BAHS@{0x58 + 0x20 * i:#x}#SM8X', st) for i, st in enumerate(('vs', 'ps', 'vs', 'ps', 'cs'))])
        self.assertEqual([c for _, _, c, _ in found], [avs[0], aps[0], avs[0], aps2[0], acs[0]])   # the control sections as stored: the key is what the game hands to NVN
        self.assertEqual(pairs, {(ss.skey(*avs), ss.skey(*aps), 'prog'), (ss.skey(*avs), ss.skey(*aps2), 'prog')})
        self.assertEqual(ss.sm8x_facts(acs[0]), ([8, 8, 1], 0x400))

    def test_agl_control_sections(self):
        avs = agl(VS)[0]
        self.assertTrue(ss.sm8x_ctl(avs)); self.assertEqual(ss.ctl_len(avs), len(avs))
        for why, (o, v) in {'+0xc4 not the length': (0xc4, 0xd0), 'compiler version kept': (0x18, 0x220014), 'hash kept': (0x24, 1)}.items():
            with self.subTest(why=why):
                c = bytearray(avs); struct.pack_into('<I', c, o, v); self.assertFalse(ss.sm8x_ctl(bytes(c)))
                found, _ = scan(bahs([[(0, (bytes(c), VS[1])), (1, agl(PS))]]))
                self.assertEqual([st for _, st, _, _ in found], ['ps'])   # the damaged entry is no shader; the other still is, and pairs with nothing

    def test_maxwell_unchanged(self):
        vs, ps = maxwell(0x0461), maxwell(0x1462)
        found, pairs = scan(bahs([[(0, vs), (1, ps)]]))
        self.assertEqual([(w, st) for w, st, _, _ in found], [('/t#BAHS@0x58', 'vs'), ('/t#BAHS@0x78', 'ps')])
        self.assertEqual(pairs, {(ss.skey(*vs), ss.skey(*ps), 'prog')})


class Ndsr(unittest.TestCase):
    def test_pack(self):
        d = ndsr([(0, VS[0]), (1, VS[1]), (3, PS[0]), (4, PS[1]), (9, bytes(0x40)), (6, CS[0]), (7, CS[1])])
        found, pairs = scan(d)
        self.assertEqual([(w.split('@')[0], st) for w, st, _, _ in found], [('/t#NDSR', 'vs'), ('/t#NDSR', 'ps'), ('/t#NDSR', 'cs')])
        self.assertTrue(all(w.endswith(ss.SM8X_TAG) for w, *_ in found)); self.assertEqual(pairs, set())
        keys = {ss.skey(c, k): (w, st, c, k) for w, st, c, k in found}
        self.assertEqual(ss.separable_pairs(keys), ({(ss.skey(*VS), ss.skey(*PS))}, [ss.skey(*PS)]))   # no program records: single-stage programs, paired with a cover

    def test_damaged(self):
        """Records that do not read back give no shader, never an exception: a kind with no code record after it, a stage the control section does not have, offsets past the end"""
        cases = {'no code record': [(0, VS[0]), (3, PS[0]), (4, PS[1])], 'stage': [(3, VS[0]), (4, VS[1])], 'code kind': [(0, VS[0]), (2, VS[1])]}
        for why, recs in cases.items():
            with self.subTest(why=why): self.assertEqual(len(ss.ndsr_shaders(ndsr(recs), '/t')), 1 if why == 'no code record' else 0)
        d = bytearray(ndsr([(0, VS[0]), (1, VS[1])]))
        for why, (o, v) in {'count': (0xa, 0xffff), 'offset': (0x10, 0xfffffff0), 'length': (0x14, 0xfffffff0)}.items():
            with self.subTest(why=why):
                x = bytearray(d); struct.pack_into('<H' if o == 0xa else '<I', x, o, v); self.assertEqual(ss.ndsr_shaders(bytes(x), '/t'), [])
        self.assertEqual(ss.ndsr_shaders(b'NDSR', '/t'), [])


class RandomDamage(unittest.TestCase):
    def test_flips_and_cuts(self):
        """Flipped bytes and cut lengths in each container: shaders that pass sm8x_check, or a '[RED] ' line, never another exception or a hang"""
        rnd = random.Random(1)
        for name, good in (('bnsh', bnsh([{'vs': VS, 'ps': PS}, {'cs': CS}])), ('bahs', bahs([[(0, agl(VS)), (1, agl(PS))]])), ('ndsr', ndsr([(0, VS[0]), (1, VS[1]), (3, PS[0]), (4, PS[1])]))):
            kept = 0
            for _ in range(600):
                d = bytearray(good if rnd.random() < 0.7 else good[:rnd.randrange(0x10, len(good))])
                for _ in range(rnd.randrange(1, 4)):
                    i = rnd.randrange(len(d)); d[i] = rnd.randrange(256)
                try: found, _ = scan(bytes(d))
                except SystemExit as e:
                    self.assertTrue(str(e).startswith('[RED] '), str(e)); continue
                for w, st, ctl, code in found:
                    if w.endswith(ss.SM8X_TAG): self.assertIsNone(ss.sm8x_check(st, ctl, code)); kept += 1
            with self.subTest(container=name): self.assertGreater(kept, 0)


if __name__ == '__main__':
    unittest.main()
