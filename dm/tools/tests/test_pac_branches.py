"""The authenticated ARMv8.3 branches (Switch 2 code) count as the plain control flow in the rewriter's checks. No game files."""
from pathlib import Path
import sys
import unittest

from capstone import Cs, CS_ARCH_ARM64, CS_MODE_ARM

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from nso_asm import PAC_BRANCH, cf_mnemonic

CASES = [(0xd65f0bff, 'retaa', 'ret'), (0xd65f0fff, 'retab', 'ret'), (0xd71f0801, 'braa', 'br'),
         (0xd73f0801, 'blraa', 'blr'), (0xd61f081f, 'braaz', 'br'), (0xd63f081f, 'blraaz', 'blr')]


class PacBranches(unittest.TestCase):
    def test_disassembled_words(self):
        md = Cs(CS_ARCH_ARM64, CS_MODE_ARM)
        for word, name, plain in CASES:
            ins = list(md.disasm(word.to_bytes(4, 'little'), 0))
            self.assertEqual(len(ins), 1, hex(word))
            self.assertEqual(ins[0].mnemonic, name)
            self.assertEqual(cf_mnemonic(ins[0].mnemonic), plain)

    def test_every_form_maps_to_a_plain_branch(self):
        for name, plain in PAC_BRANCH.items():
            self.assertIn(plain, ('ret', 'br', 'blr', 'eret'))
            self.assertEqual(cf_mnemonic(name), plain)

    def test_plain_mnemonics_unchanged(self):
        for m in ('ret', 'br', 'blr', 'b', 'bl', 'add', 'ldr', 'cbz'):
            self.assertEqual(cf_mnemonic(m), m)


if __name__ == '__main__':
    unittest.main()
