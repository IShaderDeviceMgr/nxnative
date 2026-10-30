"""NVN 2's GLSLC outputs (Switch 2: SM8x code; found on Pokémon Pokopia), read by shader_split.glslc_sm8x through glslc_shaders. Synthetic outputs built here pin down the layout the
reader takes (the whole length at +0x5c, the section table at +0xa8 with 0x98-byte headers, a 0xd8-byte control section with its own length at +0x30 and the stage at +0x54, a code area of
SPH version 4 + instructions + c1), that every shader it returns is marked SM8x (the label's '#SM8X', sm8x_ctl), that damaged outputs give nothing rather than an exception, and that Maxwell
outputs read exactly as before (the new branch takes only outputs whose +0x44 is 0, which the Maxwell reader skipped). No game files needed."""
import contextlib
import hashlib
import io
import os
import random
import struct
import sys
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(REPO / 'dm/gfx/tools'))
_had = os.environ.get('DM_GAME'); os.environ['DM_GAME'] = '01007BB017812000'   # shader_split loads a profile at import; other tests expect no default game
import shader_split as ss
if _had is None: del os.environ['DM_GAME']
else: os.environ['DM_GAME'] = _had

GLSLC_MAGIC = bytes.fromhex('91688619')
NOP = struct.pack('<QQ', 0x7918, 0x000fc00000000000)   # NOP (opcode 0x918) with a scheduling word; any bytes would do, but no magic may appear in the code
STAGE = {'vs': 0, 'ps': 1, 'gs': 2, 'hs': 3, 'ds': 4, 'cs': 5}   # control +0x54 >> 16
SPH_TYPE = {'vs': 1, 'hs': 2, 'ds': 3, 'gs': 4, 'ps': 5}


def sm8x_stage(st, ninstr=16, c1=0x100, imap=0, tg=(8, 8, 1), shared=0x400):
    """(control section, code area) of one SM8x stage. Graphics: a 0x80-byte SPH (word 0: SPH type 1 VTG / 2 pixel, version 4, shader type in bits 10..13; word 6: the pixel input map's first
    four attributes) then the instructions; compute: the instructions from +0. Then 0x80 bytes of padding and c1 bytes of the c1 segment. Control: 0xd8 bytes, +0x30 its length, +0x38 the end
    of the instructions, +0x3c c1 bytes in use, +0x40 c1's start, +0x44 the code length, +0x54 the stage << 16, compute +0x58 thread group, +0x64 shared memory"""
    code = bytearray()
    if st != 'cs':
        sph = bytearray(0x80); struct.pack_into('<I', sph, 0, (2 if st == 'ps' else 1) | 4 << 5 | SPH_TYPE[st] << 10); struct.pack_into('<I', sph, 0x18, imap); code += sph
    code += NOP * ninstr
    end = len(code); code += bytes(0x80); c1o = len(code); code += bytes(range(256))[:c1] if c1 <= 256 else bytes(c1)
    ctl = bytearray(0xd8); ctl[:4] = ss.CTL_MAGIC
    struct.pack_into('<I', ctl, 0x04, 0xc9); struct.pack_into('<II', ctl, 0x18, 0x220015, 0x288000); ctl[0x20:0x30] = hashlib.sha256(bytes(code)).digest()[:16]
    struct.pack_into('<I', ctl, 0x30, len(ctl)); struct.pack_into('<I', ctl, 0x98, len(ctl))
    struct.pack_into('<4I', ctl, 0x38, end, min(c1, 0x10), c1o, len(code))
    struct.pack_into('<I', ctl, 0x54, STAGE[st] << 16)
    if st == 'cs': struct.pack_into('<4I', ctl, 0x58, *tg, shared)
    return bytes(ctl), bytes(code)


def sm8x_output(stages, reflection=True):
    """A whole NVN 2 GLSLC output: magic, +0x08 version 0x220015, +0x0c 0x288000, +0x44 0, +0x5c the whole length, +0x60 the first section's data offset (0xa8 + 0x98 n + 0x130),
    +0x64 the section count n; section i's header at +0xa8 + 0x98 i ({size, data offset, type}; GPU code: +0x30 control offset, +0x34 code offset, +0x38 code length, +0x3c control length),
    each GPU code section its control section then its code area; a reflection section (type 3) last. The length is padded to 8 bytes"""
    n = len(stages) + bool(reflection); first = 0xa8 + 0x98 * n + 0x130
    d = bytearray(first); d[:4] = GLSLC_MAGIC
    struct.pack_into('<II', d, 0x08, 0x220015, 0x288000); struct.pack_into('<II', d, 0x60, first, n)
    for i, (ctl, code) in enumerate(stages):
        at = len(d); d += ctl + code; s = 0xa8 + 0x98 * i
        struct.pack_into('<3I', d, s, len(ctl) + len(code), at, 0); struct.pack_into('<4I', d, s + 0x30, 0, len(ctl), len(code), len(ctl))
    if reflection:
        at = len(d); d += bytes.fromhex('19292919') + bytes(0x3c); struct.pack_into('<3I', d, 0xa8 + 0x98 * len(stages), 0x40, at, 3)
    d += bytes(-len(d) % 8); struct.pack_into('<I', d, 0x5c, len(d))
    return bytes(d)


def maxwell_output():
    """A Maxwell GLSLC output as test_unity_text_glslc.py builds it: +0x44 the whole length, one 0x850-byte control section, a vertex code area right after it"""
    nctl, ncode, c = 0x850, 0x40, 0x100
    d = bytearray(c + nctl + ncode); d[:4] = GLSLC_MAGIC; struct.pack_into('<I', d, 0x44, len(d))
    d[c:c + 4] = ss.CTL_MAGIC; struct.pack_into('<I', d, c + 0x20, nctl); struct.pack_into('<I', d, c + 0x704, ncode)
    k = c + nctl; d[k:k + 4] = ss.GFX_MAGIC; struct.pack_into('<I', d, k + 0x30, 0x400)
    return bytes(d)


class Layout(unittest.TestCase):
    def test_vertex(self):
        ctl, code = sm8x_stage('vs')
        found = ss.glslc_shaders(sm8x_output([(ctl, code)]), '/g2s', need=False)
        self.assertEqual(found, [('/g2s#GLSLC@0x0#SM8X', 'vs', ctl, code)])
        self.assertTrue(ss.sm8x_ctl(ctl))
        self.assertEqual(ss.ctl_len(ctl), len(ctl))   # the whole control section is in the key
        self.assertEqual(ss.skey(ctl, code), hashlib.sha256(ctl + code).hexdigest()[:16])

    def test_every_stage(self):
        for st in ('vs', 'hs', 'ds', 'gs', 'ps', 'cs'):
            with self.subTest(st=st):
                (w, s, ctl, code), = ss.glslc_shaders(sm8x_output([sm8x_stage(st)]), '/g2s', need=False)
                self.assertEqual(s, st); self.assertTrue(w.endswith(ss.SM8X_TAG))

    def test_compute_facts(self):
        ctl, code = sm8x_stage('cs', tg=(16, 4, 2), shared=0xbc00)
        self.assertEqual(ss.sm8x_facts(ctl), ([16, 4, 2], 0xbc00))
        self.assertIsNone(ss.sm8x_check('cs', ctl, code))
        self.assertEqual(ss.glslc_sm8x(sm8x_output([sm8x_stage('cs', tg=(0, 1, 1))]), 0, '/g2s'), [])   # a compute shader needs a thread group

    def test_c1_segment(self):
        ctl, code = sm8x_stage('ps', c1=0x200)
        end, used, c1o, n = struct.unpack_from('<4I', ctl, 0x38)
        self.assertEqual((end, c1o, n), (0x80 + 16 * 16, 0x80 + 16 * 16 + 0x80, len(code)))
        self.assertEqual(ss.glslc_shaders(sm8x_output([(ctl, code)]), '/g2s', need=False)[0][3][c1o:], bytes(0x200))

    def test_several_outputs_in_a_container(self):
        a, b = sm8x_output([sm8x_stage('vs')]), sm8x_output([sm8x_stage('ps', ninstr=40)])
        blob = b'_S2G7000' + bytes(0x7d8) + a + b   # a G2S container's outputs follow each other
        found = ss.glslc_shaders(blob, '/g2s', need=False)
        self.assertEqual([(w, s) for w, s, _, _ in found], [('/g2s#GLSLC@0x7e0#SM8X', 'vs'), (f'/g2s#GLSLC@{0x7e0 + len(a):#x}#SM8X', 'ps')])

    def test_program_of_two_stages(self):
        vs, ps = sm8x_stage('vs'), sm8x_stage('ps', ninstr=8)
        found = ss.glslc_shaders(sm8x_output([vs, ps]), '/g2s', need=False)
        self.assertEqual([(w, s) for w, s, _, _ in found], [('/g2s#GLSLC@0x0#var#SM8X', 'vs'), ('/g2s#GLSLC@0x0#var#SM8X', 'ps')])
        self.assertEqual(ss.var_pairs(found), {(ss.skey(*vs), ss.skey(*ps))})
        self.assertEqual(ss.glslc_shaders(sm8x_output([vs, vs]), '/g2s', need=False), [])   # two sections of one stage are no program

    def test_pixel_inputs_read_the_sph_at_code_0(self):
        ctl, code = sm8x_stage('ps', imap=0xff00)   # attribute 1 read
        self.assertEqual(ss.ps_inputs(code), {1})
        k = maxwell_output()[0x100 + 0x850:]
        k = k[:0x30] + bytes(0x18) + struct.pack('<I', 0xff) + bytes(0x50)   # Maxwell: the SPH after the 0x30-byte header, attribute 0 read
        self.assertEqual(ss.ps_inputs(k), {0})


class Damaged(unittest.TestCase):
    def test_header_and_sections(self):
        good = sm8x_output([sm8x_stage('vs')])
        def patched(off, fmt, *v):
            d = bytearray(good); struct.pack_into(fmt, d, off, *v); return bytes(d)
        cases = {
            'truncated': good[:len(good) // 2],
            'length past the end': patched(0x5c, '<I', len(good) + 8),
            'no sections': patched(0x64, '<I', 0),
            'too many sections': patched(0x64, '<I', 0x10000),
            'section past the output': patched(0xa8 + 4, '<I', len(good) - 0x10),
            'section before the data': patched(0xa8 + 4, '<I', 0x10),
            'control length': patched(0xa8 + 0x3c, '<I', 0x500),
            'code length': patched(0xa8 + 0x38, '<I', 0x10000),
            'stage': patched(0x308 + 0x54, '<I', 4 << 16),       # tess evaluation, but the SPH says vertex
            'stage out of range': patched(0x308 + 0x54, '<I', 9 << 16),
            'control own length': patched(0x308 + 0x30, '<I', 0xe0),
            'c1 past the code': patched(0x308 + 0x40, '<I', 0x10000),
        }
        for why, d in cases.items():
            with self.subTest(why=why): self.assertEqual(ss.glslc_shaders(d, '/g2s', need=False), [])

    def test_random_damage(self):
        """Flipped bytes and cut lengths: a list (often empty), never an exception or a hang"""
        good = sm8x_output([sm8x_stage('vs'), sm8x_stage('ps')]); rnd = random.Random(1); kept = 0
        for _ in range(2000):
            d = bytearray(good if rnd.random() < 0.7 else good[:rnd.randrange(0x60, len(good))])
            for _ in range(rnd.randrange(1, 4)):
                i = rnd.randrange(len(d)); d[i] = rnd.randrange(256)
            for w, st, ctl, code in ss.glslc_shaders(bytes(d), '/g2s', need=False):
                self.assertIsNone(ss.sm8x_check(st, ctl, code)); kept += 1
        self.assertGreater(kept, 0)   # most single flips land in bytes the reader does not check


class MaxwellUnchanged(unittest.TestCase):
    def test_maxwell_output(self):
        d = maxwell_output()
        (w, st, ctl, code), = ss.glslc_shaders(d, '/x', need=False)
        self.assertEqual((w, st, len(ctl), len(code)), ('/x#GLSLC@0x0', 'vs', 0x850, 0x40))
        self.assertFalse(ss.sm8x_ctl(ctl)); self.assertFalse(ss.sph_v4(code))
        self.assertEqual(ss.ctl_len(ctl), 0x850)   # +0x20, its own length, as before
        self.assertEqual(ss.skey(ctl, code), hashlib.sha256(ctl + code).hexdigest()[:16])

    def test_mixed_blob(self):
        a, b = maxwell_output(), sm8x_output([sm8x_stage('cs')])
        found = ss.glslc_shaders(a + b, '/x', need=False)
        self.assertEqual([(w, s) for w, s, _, _ in found], [('/x#GLSLC@0x0', 'vs'), (f'/x#GLSLC@{len(a):#x}#SM8X', 'cs')])

    def test_raw_shaders_skip_sm8x_outputs(self):
        """main's loose control sections (raw_shaders) leave out those inside SM8x outputs, as those inside Maxwell outputs: their extent is +0x5c, not +0x44"""
        d = bytes(0x100) + sm8x_output([sm8x_stage('ps')])
        out = io.StringIO()
        with contextlib.redirect_stdout(out): self.assertEqual(ss.raw_shaders(d, 'main'), [])
        self.assertEqual(out.getvalue(), '')


if __name__ == '__main__':
    unittest.main()
