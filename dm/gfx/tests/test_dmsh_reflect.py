"""Production reflection conversion with synthetic translator descriptors."""
import ast
from pathlib import Path
import re
import sys
import unittest

TOOLS = Path(__file__).resolve().parents[1] / 'tools'
sys.path.insert(0, str(TOOLS))
import dmsh_abi
import dmsh_geometry


def builder_reflection():
    # Isolate pure reflection code from the builder's CLI/profile initialization.
    tree = ast.parse((TOOLS / 'dmsh_build.py').read_text())
    names = {'reflect', 'decode_tex', 'decode_ubo_tex', 'const_sampler', 'c2_array'}
    selected = [n for n in tree.body if (isinstance(n, ast.FunctionDef) and n.name in names)
                or (isinstance(n, ast.Assign) and any(isinstance(t, ast.Name) and t.id == 'SSBO' for t in n.targets))]
    ns = {'re': re, 'dmsh_geometry': dmsh_geometry, 'abi': dmsh_abi}
    exec(compile(ast.Module(body=selected, type_ignores=[]), str(TOOLS / 'dmsh_build.py'), 'exec'), ns)
    return ns['reflect']


REFLECT = builder_reflection()


def bindings(stage, offsets, flags, cb_slot=0, sm8x=False):
    source = 'struct spvDescriptorSetBuffer0\n{\n' + ''.join(
        f'    device uint* gp_s{i} [[id({i})]];\n' for i in range(len(offsets))) + '};\n'
    res = {'sbufs': [{'binding': 40 + i, 'slot': i, 'cb_slot': cb_slot,
                      'cb_offset': off // 4, 'flags': flag}
                     for i, (off, flag) in enumerate(zip(offsets, flags))]}
    return REFLECT('synthetic', stage, source, res, sm8x=sm8x)


class StorageBuffers(unittest.TestCase):
    def test_geometry_driver_table_offsets(self):
        got = bindings('gs', [0x410, 0x430], [1, 1])
        self.assertEqual([(b['kind'], b['reg']) for b in got], [('sbuf', 0), ('sbuf', 2)])

    def test_write_flag_from_translator_json(self):
        # BufferUsageFlags.Write = 1 << 0; Program.cs serializes it unchanged.
        got = bindings('cs', [0x310, 0x320], [1, 0])
        self.assertEqual([b['rw'] for b in got], [True, False])

    def test_write_flag_for_address_in_guest_uniform(self):
        got = bindings('gs', [0x38], [1], cb_slot=3)
        self.assertEqual((got[0]['kind'], got[0]['reg'], got[0]['off'], got[0]['rw']),
                         ('sbuf_ubo', 0, 0x38, True))

    def test_existing_stage_tables(self):
        for stage, base in [('vs', 0x110), ('cs', 0x310), ('ps', 0x510)]:
            with self.subTest(stage=stage):
                got = bindings(stage, [base, base + 15 * 16], [0, 0])
                self.assertEqual([b['reg'] for b in got], [0, 15])

    def test_unknown_or_unaligned_offsets_stay_unresolved(self):
        self.assertIsNone(bindings('gs', [0x414], [1])[0]['reg'])
        self.assertIsNone(bindings('hs', [0x410], [1])[0]['reg'])


class Sm8xComputeTable(unittest.TestCase):
    def test_slots_at_0x10_plus_0x10_r(self):
        # measured on Donkey Kong Bananza: compute storage buffers lie at c0 0x10 .. 0x1e0 (see dmsh_abi.h DMSH_C0_SSBO_CS_SM8X)
        got = bindings('cs', [0x10, 0x20, 0x30, 0x50, 0x1e0], [1, 1, 0, 0, 1], sm8x=True)
        self.assertEqual([b['reg'] for b in got], [0, 1, 2, 4, 29])

    def test_maxwell_compute_is_unchanged(self):
        self.assertIsNone(bindings('cs', [0x10], [1])[0]['reg'])
        self.assertEqual(bindings('cs', [0x310, 0x320], [1, 0])[1]['reg'], 1)

    def test_sm8x_graphics_keeps_maxwell_bases(self):
        self.assertEqual([b['reg'] for b in bindings('ps', [0x510, 0x530], [0, 1], sm8x=True)], [0, 2])

    def test_outside_the_table_stays_unresolved(self):
        self.assertIsNone(bindings('cs', [0x0], [0], sm8x=True)[0]['reg'])
        self.assertIsNone(bindings('cs', [0x18], [0], sm8x=True)[0]['reg'])   # not a 16-byte entry boundary
        self.assertEqual(bindings('cs', [0x200], [0], sm8x=True)[0]['reg'], 31)   # the last slot of the table
        self.assertEqual(bindings('cs', [0x210], [0], sm8x=True)[0]['kind'], 'ubo_ptr')   # past it, Maxwell's uniform table


class PushConstants(unittest.TestCase):
    def members(self, names):
        return 'struct spvDescriptorSetBuffer0\n{\n' + ''.join(f'    constant _16* {n} [[id({i})]];\n' for i, n in enumerate(names)) + '};\n'

    def test_bank_24_is_the_push_slot(self):
        # NVN 2 push constants: constant bank DMSH_PUSH_BANK is a uniform buffer at slot DMSH_PUSH_UBO_REG (dmsh_abi.h)
        got = REFLECT('synthetic', 'vs', self.members(['vp_c24', 'vp_c3', 'vp_c16']), {'sbufs': []})
        self.assertEqual([(b['kind'], b['reg']) for b in got], [('cbuf', dmsh_abi.PUSH_UBO_REG), ('cbuf', 0), ('cbuf', 13)])
        self.assertEqual(dmsh_abi.PUSH_BANK, 24)
        self.assertGreater(dmsh_abi.PUSH_UBO_REG, 13)   # above the banks c[3] to c[16] that NVN 1 games bind

    def test_driver_banks_stay_driver_banks(self):
        got = REFLECT('synthetic', 'ps', self.members(['fp_c0', 'fp_c1', 'fp_c2']), {'sbufs': []})
        self.assertEqual([(b['kind'], b['bank']) for b in got], [('drvcb', 0), ('c1', 1), ('drvcb', 2)])


if __name__ == '__main__':
    unittest.main()
