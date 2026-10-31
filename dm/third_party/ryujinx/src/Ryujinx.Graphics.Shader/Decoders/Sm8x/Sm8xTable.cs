using Ryujinx.Graphics.Shader.Instructions;
using System.Collections.Generic;

namespace Ryujinx.Graphics.Shader.Decoders.Sm8x
{
    // nxnative: how the SM8x decoder treats an opcode (bits 0..11): its emitter and its role in the control flow graph
    enum Sm8xKind : byte
    {
        Plain,
        Branch,          // BRA, CALL.REL.NOINC: a direct jump (with predicate and condition)
        Exit,            // EXIT
        Kill,            // KILL: ends the invocation, as EXIT (Maxwell's KIL ends a block too)
        Indirect,        // BRX, BRXU, RET.REL.NODEC: a jump to the program start plus a register
        Attribute,       // ALD, AST, IPA: recorded in the attribute usage
        Local,           // LDL, STL
        Shared,          // LDS, STS, ATOMS
        Store,           // STG, SUST, atomics: the shader writes memory
        Shuffle,         // SHFL
    }

    static class Sm8xTable
    {
        public readonly struct Entry
        {
            public readonly string Name;
            public readonly InstEmitter Emitter;
            public readonly Sm8xKind Kind;

            public Entry(string name, InstEmitter emitter, Sm8xKind kind)
            {
                Name = name;
                Emitter = emitter;
                Kind = kind;
            }
        }

        private static readonly Entry[] _entries = new Entry[0x1000];

        static Sm8xTable()
        {
            foreach ((ushort opcode, string name) in Sm8xNames.All)
            {
                _entries[opcode] = new Entry(name, null, Sm8xKind.Plain);
            }

            // ALU forms (bits 9..11): 1 = R,R; 2 = R,R,imm32; 3 = R,R,c[]; 4 = imm32; 5 = c[]; 6 = UR; 7 = R,R,UR (NAK encode_alu_base)
            Alu(0x020, InstEmit.Sm8xFmul, 1, 4, 5, 6);
            Alu(0x021, InstEmit.Sm8xFadd, 1, 2, 3, 4, 5, 6, 7);
            Alu(0x023, InstEmit.Sm8xFfma, 1, 2, 3, 4, 5, 6, 7);
            Alu(0x009, InstEmit.Sm8xFmnmx, 1, 4, 5, 6);
            Alu(0x008, InstEmit.Sm8xFsel, 1, 4, 5, 6);
            Alu(0x00a, InstEmit.Sm8xFset, 1, 4, 5, 6);
            Alu(0x00b, InstEmit.Sm8xFsetp, 1, 4, 5, 6);
            Alu(0x108, InstEmit.Sm8xMufu, 1, 4, 5, 6);
            Alu(0x002, InstEmit.Sm8xMov, 1, 4, 5, 6);
            Alu(0x007, InstEmit.Sm8xSel, 1, 4, 5, 6);
            Alu(0x010, InstEmit.Sm8xIadd3, 1, 2, 3, 4, 5, 6, 7);
            Alu(0x024, InstEmit.Sm8xImad, 1, 2, 3, 4, 5, 6, 7);
            Alu(0x027, InstEmit.Sm8xImadHi, 1, 2, 3, 4, 5, 6, 7);
            Alu(0x025, InstEmit.Sm8xImadWide, 1, 4);
            Alu(0x01a, InstEmit.Sm8xSgxt, 1, 4, 5, 6);
            Alu(0x00c, InstEmit.Sm8xIsetp, 1, 4, 5, 6);
            Alu(0x017, InstEmit.Sm8xImnmx, 1, 4, 5, 6);
            Alu(0x012, InstEmit.Sm8xLop3, 1, 2, 3, 4, 5, 6, 7);
            Alu(0x019, InstEmit.Sm8xShf, 1, 2, 3, 4, 5, 6, 7);
            Alu(0x011, InstEmit.Sm8xLea, 1, 2, 3, 4, 5, 6, 7);
            Alu(0x016, InstEmit.Sm8xPrmt, 1, 2, 3, 4, 5, 6, 7);
            Alu(0x045, InstEmit.Sm8xI2fp, 1, 4, 5, 6);
            Alu(0x105, InstEmit.Sm8xF2i, 1, 4, 5, 6);
            Alu(0x104, InstEmit.Sm8xF2f, 1, 4, 5, 6);
            Alu(0x106, InstEmit.Sm8xI2f, 1, 4, 5, 6);
            Alu(0x107, InstEmit.Sm8xFrnd, 1, 4, 5, 6);
            Alu(0x03e, InstEmit.Sm8xF2fp, 1, 2, 3, 4, 5, 6, 7);
            Alu(0x030, InstEmit.Sm8xHadd2, 1, 2, 3, 4, 5, 6, 7);
            Alu(0x032, InstEmit.Sm8xHmul2, 1, 2, 3, 4, 5, 6, 7);
            Alu(0x031, InstEmit.Sm8xHfma2, 1, 2, 3, 4, 5, 6, 7);
            Alu(0x040, InstEmit.Sm8xHmnmx2, 1, 4, 5, 6);
            Alu(0x034, InstEmit.Sm8xHsetp2, 1, 4, 5, 6);
            Alu(0x109, InstEmit.Sm8xPopc, 1, 4, 5, 6);
            Alu(0x100, InstEmit.Sm8xFlo, 1, 4, 5, 6);

            // Uniform datapath: the same operations on UR / UP (NAK encode_ualu: opcode + 0x80, bit 91 set; forms 1, 2, 4)
            Alu(0x090, InstEmit.Sm8xIadd3, 1, 2, 4);
            Alu(0x0a4, InstEmit.Sm8xImad, 1, 2, 4);
            Alu(0x0a5, InstEmit.Sm8xImadWide, 1, 4);
            Alu(0x09a, InstEmit.Sm8xSgxt, 1, 4);
            Alu(0x08c, InstEmit.Sm8xIsetp, 1, 4);
            Alu(0x092, InstEmit.Sm8xLop3, 1, 2, 4);
            Alu(0x099, InstEmit.Sm8xShf, 1, 2, 4);
            Alu(0x091, InstEmit.Sm8xLea, 1, 2, 4);
            Alu(0x087, InstEmit.Sm8xSel, 1, 4);
            Alu(0x0bd, InstEmit.Sm8xFlo, 1, 4);
            Alu(0x096, InstEmit.Sm8xPrmt, 1, 2, 4);
            Set(0xc82, InstEmit.Sm8xUmov);
            Set(0x882, InstEmit.Sm8xUmov);
            Set(0x89c, InstEmit.Sm8xPlop3);
            Set(0x81c, InstEmit.Sm8xPlop3);
            Set(0x3c2, InstEmit.Sm8xR2ur);
            Set(0x9c3, InstEmit.Sm8xS2r);
            Set(0x919, InstEmit.Sm8xS2r);
            Set(0x805, InstEmit.Sm8xCs2r);
            Set(0x803, InstEmit.Sm8xP2r);
            Set(0x804, InstEmit.Sm8xR2p);
            Set(0x883, InstEmit.Sm8xP2r);
            Set(0x886, InstEmit.Sm8xVote);
            Set(0x806, InstEmit.Sm8xVote);
            Set(0x3c4, InstEmit.Sm8xRedux, Sm8xKind.Shuffle);
            Set(0x3a1, InstEmit.Sm8xMatch, Sm8xKind.Shuffle);

            // Constant buffer loads
            Set(0xb82, InstEmit.Sm8xLdc);
            Set(0xab9, InstEmit.Sm8xUldc);
            Set(0xabb, InstEmit.Sm8xUldc);

            // Attributes
            Set(0x326, InstEmit.Sm8xIpa, Sm8xKind.Attribute);
            // SM8x: unverified. 0x526 is in neither NAK nor CuAssembler. It is IPA's layout (0x326 + 0x200: attribute word at 64..71, destination 16..23, bit 81..83 = PT) with a uniform
            // register in Ra (24..29) and bit 91 set. In the four Bananza pixel shaders that use it (12 instructions, all three reads of generic attribute 0 at a[0x80], a[0x84], a[0x88]) the
            // results are multiplied by the reciprocal of IPA a[0x7c] exactly as 0x326's are, and the UR operand (UR4) is 0 there: the prologue sets UR5 = URZ and UR4 = (UR5:URZ) << UR4.
            // So it is read as IPA.PASS with the UR operand ignored
            Set(0x526, InstEmit.Sm8xIpa, Sm8xKind.Attribute);
            Set(0x321, InstEmit.Sm8xAld, Sm8xKind.Attribute);
            Set(0x322, InstEmit.Sm8xAst, Sm8xKind.Attribute);
            Set(0x923, InstEmit.Sm8xIsberd);
            Set(0x34e, InstEmit.Sm8xLepc);   // SM8x: unverified (see Sm8xLepc)
            // SM8x: unverified. No source names these two; they appear only in geometry shaders (all 7 of Donkey Kong Bananza's, which have the same shape):
            //   LEPC R0; MOV R3, imm; IADD3 R0 = R0 + R3; IMAD ...; 0x357 ... OUT RZ; 0x957; EXIT
            // LEPC + the immediate (0x8a0) is exactly the address of the final OUT / 0x957 / EXIT epilogue and R0 is never used as a jump target, so 0x357 is read as registering that
            // epilogue address (implicit R0) and 0x957 as the epilogue marker: both are no-ops here. Open risk: if the hardware ran the registered epilogue at every EXIT,
            // the early-EXIT paths would also get the final OUT. That cannot matter for passthrough shaders (vertices reach the rasteriser unchanged). The line-strip shaders
            // end in EXIT right after their OUT.EMITs and never reach the epilogue OUT, so the translator has no implicit end of primitive at shader end for them; Sm8xExit adds one at every EXIT.
            Set(0x357, InstEmit.Sm8xNop);
            Set(0x957, InstEmit.Sm8xNop);
            Set(0x324, InstEmit.Sm8xOut);
            Set(0x924, InstEmit.Sm8xOut);
            Set(0xb24, InstEmit.Sm8xOut);

            // Textures bound through a constant buffer handle (NAK OpTex / OpTld / OpTld4 / OpTxd / OpTxq with TexRef::CBuf)
            Set(0xb60, InstEmit.Sm8xTex);
            Set(0xb66, InstEmit.Sm8xTld);
            Set(0xb63, InstEmit.Sm8xTld4);
            Set(0xb6c, InstEmit.Sm8xTxd);
            Set(0xb6f, InstEmit.Sm8xTxq);
            Set(0xb69, InstEmit.Sm8xTmml);

            // Memory
            Set(0x983, InstEmit.Sm8xLdl, Sm8xKind.Local);
            Set(0x387, InstEmit.Sm8xStl, Sm8xKind.Local);
            Set(0x987, InstEmit.Sm8xStl, Sm8xKind.Local);
            Set(0x984, InstEmit.Sm8xLds, Sm8xKind.Shared);
            Set(0x388, InstEmit.Sm8xSts, Sm8xKind.Shared);
            Set(0x988, InstEmit.Sm8xSts, Sm8xKind.Shared);

            // Global memory (no UR address and no predicate here) and bound surfaces (0xb9b SUST.P, 0xb97 SULD.P: the opcode note)
            Set(0x981, InstEmit.Sm8xLdg);
            Set(0x381, InstEmit.Sm8xLdg);
            Set(0x986, InstEmit.Sm8xStg, Sm8xKind.Store);
            Set(0x386, InstEmit.Sm8xStg, Sm8xKind.Store);
            Set(0x9a8, InstEmit.Sm8xAtomg, Sm8xKind.Store);
            Set(0x98e, InstEmit.Sm8xRed, Sm8xKind.Store);
            Set(0x3a8, InstEmit.Sm8xAtomg, Sm8xKind.Store);
            Set(0xb9b, InstEmit.Sm8xSust, Sm8xKind.Store);
            Set(0xb97, InstEmit.Sm8xSuld);
            Alu(0x013, InstEmit.Sm8xIabs, 1, 4, 5, 6);

            // Warp
            Set(0x822, InstEmit.Sm8xFswzadd);
            Set(0x389, InstEmit.Sm8xShfl, Sm8xKind.Shuffle);
            Set(0x589, InstEmit.Sm8xShfl, Sm8xKind.Shuffle);
            Set(0x989, InstEmit.Sm8xShfl, Sm8xKind.Shuffle);
            Set(0xf89, InstEmit.Sm8xShfl, Sm8xKind.Shuffle);

            // Control flow. BSSY / BSYNC / BREAK / WARPSYNC / BMOV manage Volta's convergence barriers: they decide when a warp's threads run together again, never
            // where one thread goes next (the jumps are explicit BRA), so a translation that runs each invocation on its own needs none of them
            Set(0x947, InstEmit.Sm8xBra, Sm8xKind.Branch);
            Set(0x944, InstEmit.Sm8xBra, Sm8xKind.Branch);
            Set(0x94d, InstEmit.Sm8xExit, Sm8xKind.Exit);
            Set(0x949, InstEmit.Sm8xIndirect, Sm8xKind.Indirect);
            Set(0x958, InstEmit.Sm8xIndirect, Sm8xKind.Indirect);
            Set(0x950, InstEmit.Sm8xIndirect, Sm8xKind.Indirect);
            Set(0x95b, InstEmit.Sm8xKill, Sm8xKind.Kill);
            Set(0x945, InstEmit.Sm8xNop);   // BSSY
            Set(0x941, InstEmit.Sm8xNop);   // BSYNC
            Set(0x942, InstEmit.Sm8xNop);   // BREAK
            Set(0x948, InstEmit.Sm8xNop);   // WARPSYNC
            Set(0x148, InstEmit.Sm8xNop);   // WARPSYNC (register mask)
            Set(0xf55, InstEmit.Sm8xNop);   // BMOV.32 B, MACTIVE
            Set(0xf56, InstEmit.Sm8xNop);   // BMOV.32 MACTIVE, B
            Set(0x91a, InstEmit.Sm8xNop);   // DEPBAR: a scoreboard wait
            Set(0x918, InstEmit.Sm8xNop);   // NOP
            Set(0x9ab, InstEmit.Sm8xNop);   // ERRBAR
            Set(0x98f, InstEmit.Sm8xNop);   // CCTL
            Set(0x992, InstEmit.Sm8xMembar);
            Set(0xb1d, InstEmit.Sm8xBar);
        }

        private static void Alu(int opcode, InstEmitter emitter, params int[] forms)
        {
            foreach (int form in forms)
            {
                Set((form << 9) | opcode, emitter);
            }
        }

        private static void Set(int opcode, InstEmitter emitter, Sm8xKind kind = Sm8xKind.Plain)
        {
            _entries[opcode] = new Entry(_entries[opcode].Name ?? $"OP_{opcode:x3}", emitter, kind);
        }

        public static Entry Get(int opcode) => _entries[opcode & 0xfff];

        public static string GetName(int opcode) => _entries[opcode & 0xfff].Name ?? $"OP_{opcode & 0xfff:x3}";

        public static IEnumerable<int> Emitted()
        {
            for (int i = 0; i < _entries.Length; i++)
            {
                if (_entries[i].Emitter != null)
                {
                    yield return i;
                }
            }
        }

        // An instruction that never runs: predicate !PT
        public static bool IsNever(Sm8xInst inst) => inst.Pred == RegisterConsts.PredicateTrueIndex && inst.PredInv;

        // A branch, exit or indirect jump whose own condition (87..90) is !PT never jumps
        public static bool IsNeverTaken(Sm8xInst inst, Sm8xKind kind)
        {
            return IsNever(inst) || (kind is Sm8xKind.Branch or Sm8xKind.Exit or Sm8xKind.Kill && inst.SrcPred == RegisterConsts.PredicateTrueIndex && inst.SrcPredInv);
        }

        public static bool IsConditional(Sm8xInst inst, Sm8xKind kind)
        {
            bool predicated = inst.Pred != RegisterConsts.PredicateTrueIndex || inst.PredInv;
            bool condition = kind is Sm8xKind.Branch or Sm8xKind.Exit or Sm8xKind.Kill && (inst.SrcPred != RegisterConsts.PredicateTrueIndex || inst.SrcPredInv);

            return predicated || condition;
        }

        public static bool IsTerminator(Sm8xInst inst, out Sm8xKind kind)
        {
            kind = Get(inst.Opcode).Kind;

            return kind is Sm8xKind.Branch or Sm8xKind.Exit or Sm8xKind.Kill or Sm8xKind.Indirect && !IsNeverTaken(inst, kind);
        }

        public static bool IsUnconditionalBranch(InstOp op)
        {
            Sm8xInst inst = new(op);

            return IsTerminator(inst, out Sm8xKind kind) && !IsConditional(inst, kind);
        }
    }
}

namespace Ryujinx.Graphics.Shader
{
    // nxnative: what the SM8x decoder knows about an opcode (bits 0..11), for tools that report coverage
    public static class Sm8xOpcodes
    {
        public static string Name(int opcode) => Decoders.Sm8x.Sm8xTable.GetName(opcode);

        public static bool HasEmitter(int opcode) => Decoders.Sm8x.Sm8xTable.Get(opcode).Emitter != null;

        public static System.Collections.Generic.IEnumerable<int> WithEmitter() => Decoders.Sm8x.Sm8xTable.Emitted();
    }
}
