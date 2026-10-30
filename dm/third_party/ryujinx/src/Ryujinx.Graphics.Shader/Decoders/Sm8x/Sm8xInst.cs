namespace Ryujinx.Graphics.Shader.Decoders.Sm8x
{
    // nxnative: one SM8x (Volta+, Switch 2's Ampere-class GPU) instruction, 128 bits. Field positions follow Mesa NAK's encoder (sm70_encode.rs, MIT licence) and
    // CuAssembler's sm_86 tables (MIT licence); the scheduling bits 105..127 (stall, yield, barriers, reuse) carry no meaning for translation.
    readonly struct Sm8xInst
    {
        public readonly ulong Lo;
        public readonly ulong Hi;

        public Sm8xInst(ulong lo, ulong hi)
        {
            Lo = lo;
            Hi = hi;
        }

        public Sm8xInst(InstOp op) : this(op.RawOpCode, op.RawOpCodeHi)
        {
        }

        public int Bits(int start, int count)
        {
            ulong v;

            if (start >= 64)
            {
                v = Hi >> (start - 64);
            }
            else if (start + count <= 64)
            {
                v = Lo >> start;
            }
            else
            {
                v = (Lo >> start) | (Hi << (64 - start));
            }

            return (int)(v & ((1UL << count) - 1));
        }

        public bool Bit(int index) => Bits(index, 1) != 0;

        // Bits 0..11; bits 9..11 are the ALU operand form (NAK encode_alu_base): 1 R,R  2 R,R,imm  3 R,R,c[]  4 imm  5 c[]  6 UR  7 R,R,UR
        public int Opcode => (int)(Lo & 0xfff);
        public int Form => (int)((Lo >> 9) & 7);

        // Instruction predicate: bits 12..14, negated by bit 15 (PT = 7)
        public int Pred => Bits(12, 3);
        public bool PredInv => Bit(15);

        public int Rd => Bits(16, 8);
        public int Ra => Bits(24, 8);
        public int Rb => Bits(32, 8);
        public int Rc => Bits(64, 8);

        public int URd => Bits(16, 6);
        public int URa => Bits(24, 6);
        public int URb => Bits(32, 6);
        public int URc => Bits(64, 6);

        public int Imm32 => (int)(uint)(Lo >> 32);

        // Constant buffer operand of the ALU forms: bank 54..58, byte offset 38..53 (NAK set_src_cb); bit 91 makes it bindless (a UR handle), which the census never sees
        public int CbufSlot => Bits(54, 5);
        public int CbufByteOffset => Bits(38, 16);

        // Branch target: signed byte offset in bits 32..81 from the next instruction (NAK set_rel_offset 34..82 counts words, so the offset's low two bits, 32..33, are
        // not part of it: BRA keeps its .U flag (all threads of the warp agree) in bit 32, which a BRA.U would otherwise turn into an unaligned target)
        public long BranchOffset => (((long)((Lo >> 32) | (Hi << 32)) << 14) >> 14) & ~3L;

        // Second predicate operand of most instructions: 87..89, negated by 90
        public int SrcPred => Bits(87, 3);
        public bool SrcPredInv => Bit(90);

        public bool UniformOperand => Bit(91);
    }
}
