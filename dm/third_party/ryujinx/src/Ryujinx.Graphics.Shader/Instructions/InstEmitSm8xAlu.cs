using Ryujinx.Graphics.Shader.Decoders;
using Ryujinx.Graphics.Shader.Decoders.Sm8x;
using Ryujinx.Graphics.Shader.IntermediateRepresentation;
using Ryujinx.Graphics.Shader.Translation;
using static Ryujinx.Graphics.Shader.Instructions.InstEmitSm8xHelper;
using static Ryujinx.Graphics.Shader.IntermediateRepresentation.OperandHelper;

namespace Ryujinx.Graphics.Shader.Instructions
{
    static class InstEmitSm8xHelper
    {
        public static Sm8xInst I(EmitterContext context) => Sm8xHelper.Inst(context);
    }

    // nxnative: SM8x (Volta+) ALU instructions, emitted as the same IR the Maxwell emitters produce. Bit fields: Mesa NAK sm70_encode.rs (MIT licence) and CuAssembler's
    // sm_86 tables (MIT licence, via the opcode map); semantics: NAK's IR and the PTX ISA. Marked "SM8x: unverified" where the encoding gives a field no source pins down.
    static partial class InstEmit
    {
        // FADD: A + B; .SAT 77, rounding 78..79 (the IR has one rounding), .FTZ 80
        public static void Sm8xFadd(EmitterContext context)
        {
            Sm8xInst inst = I(context);
            Operand a = Sm8xHelper.F32(context, Sm8xHelper.SrcA(inst, false));
            Operand b = Sm8xHelper.F32(context, Sm8xHelper.SrcSecond(context, inst, false));

            Sm8xHelper.SetGpr(context, inst.Rd, context.FPSaturate(context.FPAdd(a, b), inst.Bit(77)));
        }

        // FMUL: A * B, scaled by bits 84..86 (4 = none, 3 = .D2, 2 = .D4 from CuAssembler's records; 1 = .D8, 5..7 = .M2/.M4/.M8 by analogy), .SAT 77
        public static void Sm8xFmul(EmitterContext context)
        {
            Sm8xInst inst = I(context);
            Operand a = Sm8xHelper.F32(context, Sm8xHelper.SrcA(inst, false));
            Operand b = Sm8xHelper.F32(context, Sm8xHelper.SrcSecond(context, inst, false));

            int scale = inst.Bits(84, 3);

            if (scale != 4)
            {
                // SM8x: unverified for .D8 and the .M forms
                float factor = scale switch { 3 => 0.5f, 2 => 0.25f, 1 => 0.125f, 5 => 2f, 6 => 4f, 7 => 8f, _ => 1f };
                a = context.FPMultiply(a, ConstF(factor));
            }

            Sm8xHelper.SetGpr(context, inst.Rd, context.FPSaturate(context.FPMultiply(a, b), inst.Bit(77)));
        }

        // FFMA: A * B + C, .SAT 77
        public static void Sm8xFfma(EmitterContext context)
        {
            Sm8xInst inst = I(context);
            Operand a = Sm8xHelper.F32(context, Sm8xHelper.SrcA(inst, false));
            (Sm8xSrc bs, Sm8xSrc cs) = Sm8xHelper.SrcBC(context, inst, false);

            Operand res = context.FPFusedMultiplyAdd(a, Sm8xHelper.F32(context, bs), Sm8xHelper.F32(context, cs));

            Sm8xHelper.SetGpr(context, inst.Rd, context.FPSaturate(res, inst.Bit(77)));
        }

        // FMNMX: min when the predicate 87..90 is true, else max (NAK OpFMnMx); .NAN 81 (SM8x: unverified, NaN propagation not modelled)
        public static void Sm8xFmnmx(EmitterContext context)
        {
            Sm8xInst inst = I(context);
            Operand a = Sm8xHelper.F32(context, Sm8xHelper.SrcA(inst, false));
            Operand b = Sm8xHelper.F32(context, Sm8xHelper.SrcSecond(context, inst, false));
            Operand isMin = Sm8xHelper.Pred(context, inst.SrcPred, inst.SrcPredInv);

            Operand res = context.ConditionalSelect(isMin, context.FPMinimum(a, b), context.FPMaximum(a, b));

            Sm8xHelper.SetGpr(context, inst.Rd, res);
        }

        // FSEL: A when the predicate 87..90 is true, else B
        public static void Sm8xFsel(EmitterContext context)
        {
            Sm8xInst inst = I(context);
            Operand a = Sm8xHelper.F32(context, Sm8xHelper.SrcA(inst, false));
            Operand b = Sm8xHelper.F32(context, Sm8xHelper.SrcSecond(context, inst, false));

            Sm8xHelper.SetGpr(context, inst.Rd, context.ConditionalSelect(Sm8xHelper.Pred(context, inst.SrcPred, inst.SrcPredInv), a, b));
        }

        // SEL: integer FSEL
        public static void Sm8xSel(EmitterContext context)
        {
            Sm8xInst inst = I(context);
            bool u = Sm8xHelper.IsUniformAlu(inst);
            Operand a = Sm8xHelper.SrcA(inst, u).Value;
            Operand b = Sm8xHelper.SrcSecond(context, inst, u).Value;

            Sm8xHelper.SetReg(context, inst.Rd, context.ConditionalSelect(Sm8xHelper.Pred(context, inst.SrcPred, inst.SrcPredInv, u), a, b), u);
        }

        // FSET.BF: 1.0 or 0.0 from the comparison 76..79 (the encoding of Maxwell's FComp), combined (74..75, as FSETP: SM8x: unverified) with the predicate 87..90
        public static void Sm8xFset(EmitterContext context)
        {
            Sm8xInst inst = I(context);
            Operand a = Sm8xHelper.F32(context, Sm8xHelper.SrcA(inst, false));
            Operand b = Sm8xHelper.F32(context, Sm8xHelper.SrcSecond(context, inst, false));

            Operand res = GetFPComparison(context, (FComp)inst.Bits(76, 4), a, b);
            res = InstEmitAluHelper.GetPredLogicalOp(context, (BoolOp)inst.Bits(74, 2), res, Sm8xHelper.Pred(context, inst.SrcPred, inst.SrcPredInv));

            Sm8xHelper.SetGpr(context, inst.Rd, context.ConditionalSelect(res, ConstF(1f), Const(0)));
        }

        // FSETP: P(81..83) = cmp op accumulator, P(84..86) = !cmp op accumulator (NAK OpFSetP; Maxwell's FSETP pair)
        public static void Sm8xFsetp(EmitterContext context)
        {
            Sm8xInst inst = I(context);
            Operand a = Sm8xHelper.F32(context, Sm8xHelper.SrcA(inst, false));
            Operand b = Sm8xHelper.F32(context, Sm8xHelper.SrcSecond(context, inst, false));

            Operand res = GetFPComparison(context, (FComp)inst.Bits(76, 4), a, b);
            EmitSm8xPredPair(context, inst, res, false);
        }

        private static void EmitSm8xPredPair(EmitterContext context, Sm8xInst inst, Operand res, bool uniform, int setOpBit = 74)
        {
            Operand acc = Sm8xHelper.Pred(context, inst.SrcPred, inst.SrcPredInv, uniform);
            BoolOp setOp = (BoolOp)inst.Bits(setOpBit, 2);

            Operand p0 = InstEmitAluHelper.GetPredLogicalOp(context, setOp, res, acc);
            Operand p1 = InstEmitAluHelper.GetPredLogicalOp(context, setOp, context.BitwiseNot(res), acc);

            Sm8xHelper.SetPred(context, inst.Bits(81, 3), p0, uniform);
            Sm8xHelper.SetPred(context, inst.Bits(84, 3), p1, uniform);
        }

        // MUFU: function 74..79 in Maxwell's MufuOp numbering (NAK OpMuFu), on the second source
        public static void Sm8xMufu(EmitterContext context)
        {
            Sm8xInst inst = I(context);

            // .F16 (73, NAK below SM90): the source is one half (60..61: 0 low, 1 high, NAK's F16 swizzle), the result a half in the low 16 bits (SM8x: unverified,
            // the upper half's value; Pokopia's MUFU.RSQ.F16 results go to HFMA2 / HMUL2)
            bool f16 = inst.Bit(73);
            Sm8xSrc src = Sm8xHelper.SrcSecond(context, inst, false);
            Operand res = f16
                ? context.FPAbsNeg(inst.Bits(60, 2) == 1 ? context.UnpackHalf2x16High(src.Value) : context.UnpackHalf2x16Low(src.Value), src.Abs, src.Neg)
                : Sm8xHelper.F32(context, src);

            res = (MufuOp)inst.Bits(74, 6) switch
            {
                MufuOp.Cos => context.FPCosine(res),
                MufuOp.Sin => context.FPSine(res),
                MufuOp.Ex2 => context.FPExponentB2(res),
                MufuOp.Lg2 => context.FPLogarithmB2(res),
                MufuOp.Rcp => context.FPReciprocal(res),
                MufuOp.Rsq => context.FPReciprocalSquareRoot(res),
                MufuOp.Rcp64h => context.UnpackDouble2x32High(context.FPReciprocal(context.PackDouble2x32(Const(0), res), Instruction.FP64)),
                MufuOp.Rsq64h => context.UnpackDouble2x32High(context.FPReciprocalSquareRoot(context.PackDouble2x32(Const(0), res), Instruction.FP64)),
                MufuOp.Sqrt => context.FPSquareRoot(res),
                _ => null,
            };

            if (res == null)
            {
                Sm8xHelper.Unsupported(context, $"function {inst.Bits(74, 6)}");
            }

            Sm8xHelper.SetGpr(context, inst.Rd, f16 ? context.PackHalf2x16(res, ConstF(0)) : res);
        }

        // MOV: the second source; the quad lane mask 72..75 (0xf in the census) only matters across lanes
        public static void Sm8xMov(EmitterContext context)
        {
            Sm8xInst inst = I(context);

            Sm8xHelper.SetGpr(context, inst.Rd, Sm8xHelper.SrcSecond(context, inst, false).Value);
        }

        // UMOV: URd = UR at 32 (0xc82) or imm32 (0x882)
        public static void Sm8xUmov(EmitterContext context)
        {
            Sm8xInst inst = I(context);
            Operand src = inst.Form == 4 ? Const(inst.Imm32) : Sm8xHelper.Ur(inst.URb);

            Sm8xHelper.SetUr(context, inst.URd, src);
        }

        // R2UR: URd = Ra
        public static void Sm8xR2ur(EmitterContext context)
        {
            Sm8xInst inst = I(context);

            Sm8xHelper.SetUr(context, inst.URd, Sm8xHelper.Gpr(inst.Ra));
        }

        // IADD3: A + B + C; - at 72 / 63 / 75 negates; .X (74) adds the carries 87..90 and 77..80 and makes - a bitwise not; carries out to 81..83 (A + B) and 84..86 (+ C).
        // SM8x: unverified, which carry each output predicate receives when both are written
        public static void Sm8xIadd3(EmitterContext context)
        {
            Sm8xInst inst = I(context);
            bool u = Sm8xHelper.IsUniformAlu(inst);
            bool x = inst.Bit(74);
            (Sm8xSrc bs, Sm8xSrc cs) = Sm8xHelper.SrcBC(context, inst, u);

            Operand a = Sm8xHelper.I32(context, Sm8xHelper.SrcA(inst, u), x);
            Operand b = Sm8xHelper.I32(context, bs, x);
            Operand c = Sm8xHelper.I32(context, cs, x);

            int p0 = inst.Bits(81, 3), p1 = inst.Bits(84, 3);
            bool carries = p0 != RegisterConsts.PredicateTrueIndex || p1 != RegisterConsts.PredicateTrueIndex;

            Operand t = context.IAdd(a, b);
            Operand k0 = carries ? context.ICompareLessUnsigned(t, a) : null;

            if (x)
            {
                Operand cin = context.ConditionalSelect(Sm8xHelper.Pred(context, inst.SrcPred, inst.SrcPredInv, u), Const(1), Const(0));
                Operand t2 = context.IAdd(t, cin);
                k0 = carries ? context.BitwiseOr(k0, context.ICompareLessUnsigned(t2, t)) : null;
                t = t2;
            }

            Operand s = context.IAdd(t, c);
            Operand k1 = carries ? context.ICompareLessUnsigned(s, t) : null;

            if (x)
            {
                Operand cin = context.ConditionalSelect(Sm8xHelper.Pred(context, inst.Bits(77, 3), inst.Bit(80), u), Const(1), Const(0));
                Operand s2 = context.IAdd(s, cin);
                k1 = carries ? context.BitwiseOr(k1, context.ICompareLessUnsigned(s2, s)) : null;
                s = s2;
            }

            if (carries)
            {
                Sm8xHelper.SetPred(context, p0, k0, u);
                Sm8xHelper.SetPred(context, p1, k1, u);
            }

            Sm8xHelper.SetReg(context, inst.Rd, s, u);
        }

        // IMAD: A * B + C (the low 32 bits do not depend on .U32 / signed 73); - on C (75 or 63). IMAD.MOV / .SHL / .IADD are spellings of this.
        // .X (74, CuAssembler's IMAD.X records and NAK's .X bit for IADD3): adds the carry predicate 87..90 (negated by 90), and the - on C is then a bitwise not (as IADD3.X);
        // the carry out that IADD3.X writes is not produced (the records always have 81..83 = PT)
        public static void Sm8xImad(EmitterContext context)
        {
            Sm8xInst inst = I(context);
            bool u = Sm8xHelper.IsUniformAlu(inst);
            bool x = inst.Bit(74);

            if (x && inst.Bits(81, 3) != RegisterConsts.PredicateTrueIndex)
            {
                Sm8xHelper.Unsupported(context, ".X with a carry out");
            }

            (Sm8xSrc bs, Sm8xSrc cs) = Sm8xHelper.SrcBC(context, inst, u);
            Operand a = Sm8xHelper.SrcA(inst, u).Value;
            Operand res = context.IAdd(context.IMultiply(a, bs.Value), Sm8xHelper.I32(context, cs, x));

            if (x)
            {
                Operand cin = context.ConditionalSelect(Sm8xHelper.Pred(context, inst.SrcPred, inst.SrcPredInv, u), Const(1), Const(0));
                res = context.IAdd(res, cin);
            }

            Sm8xHelper.SetReg(context, inst.Rd, res, u);
        }

        // IMAD.WIDE (0x?25) and UIMAD.WIDE (0x?a5): Rd:Rd+1 = A * B + the 64-bit pair C (C is a register pair in the forms accepted here: 1 and 4), the product signed when
        // 73 is set (.U32 clears it; NAK OpIMad64). A negated C (75) and .X are rejected
        public static void Sm8xImadWide(EmitterContext context)
        {
            Sm8xInst inst = I(context);
            bool u = Sm8xHelper.IsUniformAlu(inst);

            if (inst.Bit(74) || inst.Bit(75) || inst.Bit(63) || inst.Bits(81, 3) != RegisterConsts.PredicateTrueIndex)
            {
                Sm8xHelper.Unsupported(context, ".X, negated C or a carry out");
            }

            (Sm8xSrc bs, _) = Sm8xHelper.SrcBC(context, inst, u);
            Operand a = Sm8xHelper.SrcA(inst, u).Value;
            int rc = u ? inst.URc : inst.Rc;
            int zero = u ? RegisterConsts.UniformZeroIndex : RegisterConsts.RegisterZeroIndex;
            Operand cLo = Sm8xHelper.Reg(rc, u);
            Operand cHi = rc >= zero ? Const(0) : Sm8xHelper.Reg(rc + 1, u);

            Operand lo = context.IMultiply(a, bs.Value);
            Operand hi = inst.Bit(73) ? context.MultiplyHighS32(a, bs.Value) : context.MultiplyHighU32(a, bs.Value);
            Operand sumLo = context.IAdd(lo, cLo);
            Operand carry = context.ConditionalSelect(context.ICompareLessUnsigned(sumLo, lo), Const(1), Const(0));
            Operand sumHi = context.IAdd(context.IAdd(hi, cHi), carry);

            Sm8xHelper.SetReg(context, inst.Rd, sumLo, u);

            if (inst.Rd < zero)
            {
                Sm8xHelper.SetReg(context, inst.Rd + 1, sumHi, u);
            }
        }

        // SGXT: Rd = the low B bits of A (B from 1 to 32) sign-extended, or zero-extended with .U32 (73 clear); B is clamped to 32 (.W, 75, wraps instead: rejected).
        // CuAssembler's records: SGXT R7, R7, 0x8 is a signed byte, SGXT.U32 R3, R11, 0x1 keeps one bit. SM8x: unverified, B = 0 (the field extract gives 0)
        public static void Sm8xSgxt(EmitterContext context)
        {
            Sm8xInst inst = I(context);
            bool u = Sm8xHelper.IsUniformAlu(inst);

            if (inst.Bit(75))
            {
                Sm8xHelper.Unsupported(context, ".W");
            }

            Operand a = Sm8xHelper.SrcA(inst, u).Value;
            Operand bits = Sm8xHelper.SrcSecond(context, inst, u).Value;

            if (bits.Type == OperandType.Constant)
            {
                bits = Const(System.Math.Min(bits.Value, 32));
            }
            else
            {
                bits = context.IMinimumU32(bits, Const(32));
            }

            Operand res = inst.Bit(73) ? context.BitfieldExtractS32(a, Const(0), bits) : context.BitfieldExtractU32(a, Const(0), bits);

            Sm8xHelper.SetReg(context, inst.Rd, res, u);
        }

        // IMAD.HI: the high 32 bits of A * B (signed when 73), plus C. SM8x: unverified, whether C is added with a carry into bit 32 of the full product
        public static void Sm8xImadHi(EmitterContext context)
        {
            Sm8xInst inst = I(context);
            bool u = Sm8xHelper.IsUniformAlu(inst);
            (Sm8xSrc bs, Sm8xSrc cs) = Sm8xHelper.SrcBC(context, inst, u);
            Operand a = Sm8xHelper.SrcA(inst, u).Value;
            Operand hi = inst.Bit(73) ? context.MultiplyHighS32(a, bs.Value) : context.MultiplyHighU32(a, bs.Value);

            Sm8xHelper.SetReg(context, inst.Rd, context.IAdd(hi, Sm8xHelper.I32(context, cs)), u);
        }

        // ISETP: comparison 76..78 (Maxwell's IComp numbering), signed 73, combine 74..75 with the accumulator 87..90; .EX (72) compares high words with the low
        // words' result in 68..71 (NAK OpISetP low_cmp)
        public static void Sm8xIsetp(EmitterContext context)
        {
            Sm8xInst inst = I(context);
            bool u = Sm8xHelper.IsUniformAlu(inst);
            Operand a = Sm8xHelper.SrcA(inst, u).Value;
            Operand b = Sm8xHelper.SrcSecond(context, inst, u).Value;
            IComp cmp = (IComp)inst.Bits(76, 3);
            bool signed = inst.Bit(73);

            Operand res = GetIntComparison(context, cmp, a, b, signed);

            if (inst.Bit(72) && cmp is not IComp.F and not IComp.T)
            {
                Operand low = Sm8xHelper.Pred(context, inst.Bits(68, 3), inst.Bit(71), u);
                Operand eq = context.ICompareEqual(a, b);

                res = cmp switch
                {
                    IComp.Eq => context.BitwiseAnd(eq, low),
                    IComp.Ne => context.BitwiseOr(res, low),
                    _ => context.BitwiseOr(res, context.BitwiseAnd(eq, low)),
                };
            }

            EmitSm8xPredPair(context, inst, res, u);
        }

        // IMNMX: min when the predicate 87..90 is true, else max; signed 73
        public static void Sm8xImnmx(EmitterContext context)
        {
            Sm8xInst inst = I(context);
            bool u = Sm8xHelper.IsUniformAlu(inst);
            Operand a = Sm8xHelper.SrcA(inst, u).Value;
            Operand b = Sm8xHelper.SrcSecond(context, inst, u).Value;
            Operand isMin = Sm8xHelper.Pred(context, inst.SrcPred, inst.SrcPredInv, u);

            Operand min = inst.Bit(73) ? context.IMinimumS32(a, b) : context.IMinimumU32(a, b);
            Operand max = inst.Bit(73) ? context.IMaximumS32(a, b) : context.IMaximumU32(a, b);

            Sm8xHelper.SetReg(context, inst.Rd, context.ConditionalSelect(isMin, min, max), u);
        }

        // LOP3.LUT: truth table 72..79 over A, B, C; a destination predicate 81..83 gets result != 0 (SM8x: unverified, the .PAND variant at 80 is not modelled)
        public static void Sm8xLop3(EmitterContext context)
        {
            Sm8xInst inst = I(context);
            bool u = Sm8xHelper.IsUniformAlu(inst);
            (Sm8xSrc bs, Sm8xSrc cs) = Sm8xHelper.SrcBC(context, inst, u);

            Operand res = Lop3Expression.GetFromTruthTable(context, Sm8xHelper.SrcA(inst, u).Value, bs.Value, cs.Value, inst.Bits(72, 8));

            Sm8xHelper.SetPred(context, inst.Bits(81, 3), context.ICompareNotEqual(res, Const(0)), u);
            Sm8xHelper.SetReg(context, inst.Rd, res, u);
        }

        // PLOP3.LUT: P(81..83) = lut0(P87, P77, P68), P(84..86) = lut1(...); lut0 = 64..66 | 72..76 << 3, lut1 = 16..23; a uniform predicate in the third source when bit 67
        public static void Sm8xPlop3(EmitterContext context)
        {
            Sm8xInst inst = I(context);
            bool u = inst.Opcode == 0x89c;

            Operand a = Sm8xHelper.Pred(context, inst.Bits(87, 3), inst.Bit(90), u);
            Operand b = Sm8xHelper.Pred(context, inst.Bits(77, 3), inst.Bit(80), u);
            Operand c = Sm8xHelper.Pred(context, inst.Bits(68, 3), inst.Bit(71), u || inst.Bit(67));

            int lut0 = inst.Bits(64, 3) | (inst.Bits(72, 5) << 3);
            int lut1 = inst.Bits(16, 8);

            Sm8xHelper.SetPred(context, inst.Bits(81, 3), Lop3Expression.GetFromTruthTable(context, a, b, c, lut0), u);
            Sm8xHelper.SetPred(context, inst.Bits(84, 3), Lop3Expression.GetFromTruthTable(context, a, b, c, lut1), u);
        }

        // SHF: funnel shift of the 64-bit value C:A (A low) by B. Type 73..74 (0 S64, 1 U64, 2 S32, 3 U32), .W 75 (wrap the shift instead of clamping), .R 76, .HI 80 (keep
        // the high word). The 32-bit types clamp the shift to 32 (NAK OpShf)
        public static void Sm8xShf(EmitterContext context)
        {
            Sm8xInst inst = I(context);
            bool u = Sm8xHelper.IsUniformAlu(inst);
            (Sm8xSrc bs, Sm8xSrc cs) = Sm8xHelper.SrcBC(context, inst, u);

            Operand lo = Sm8xHelper.SrcA(inst, u).Value;
            Operand n = bs.Value;
            Operand hi = cs.Value;

            int type = inst.Bits(73, 2);
            bool is64 = type < 2;
            bool signed = type is 0 or 2;
            bool wrap = inst.Bit(75);
            bool right = inst.Bit(76);
            bool high = inst.Bit(80);

            int max = is64 ? 64 : 32;
            n = wrap ? context.BitwiseAnd(n, Const(max - 1)) : context.IMinimumU32(n, Const(max));

            Operand res = EmitSm8xFunnel(context, lo, hi, n, right, signed, high);

            Sm8xHelper.SetReg(context, inst.Rd, res, u);
        }

        // One word of (hi:lo) shifted by n in 0..64, without shifts of 32 or more (undefined in SPIR-V)
        private static Operand EmitSm8xFunnel(EmitterContext context, Operand lo, Operand hi, Operand n, bool right, bool signed, bool high)
        {
            Operand isZero = context.ICompareEqual(n, Const(0));
            Operand ge32 = context.ICompareGreaterOrEqualUnsigned(n, Const(32));
            Operand m = context.BitwiseAnd(n, Const(31));                 // n mod 32
            Operand inv = context.ISubtract(Const(32), m);                 // 32 - (n mod 32), 32 when m = 0 (only used when m != 0)
            Operand mZero = context.ICompareEqual(m, Const(0));
            Operand sign = signed ? context.ShiftRightS32(hi, Const(31)) : Const(0);

            Operand res;

            if (!right)
            {
                // result = (hi:lo) << n
                Operand loPart = context.ShiftLeft(lo, m);
                Operand hiPart = context.ConditionalSelect(mZero, hi, context.BitwiseOr(context.ShiftLeft(hi, m), context.ShiftRightU32(lo, inv)));
                Operand loGe = Const(0);
                Operand hiGe = context.ConditionalSelect(context.ICompareEqual(n, Const(64)), Const(0), context.ShiftLeft(lo, m));

                res = high ? context.ConditionalSelect(ge32, hiGe, hiPart) : context.ConditionalSelect(ge32, loGe, loPart);
            }
            else
            {
                // result = (hi:lo) >> n
                Operand hiShift = signed ? context.ShiftRightS32(hi, m) : context.ShiftRightU32(hi, m);
                Operand loPart = context.ConditionalSelect(mZero, lo, context.BitwiseOr(context.ShiftRightU32(lo, m), context.ShiftLeft(hi, inv)));
                Operand loGe = context.ConditionalSelect(context.ICompareEqual(n, Const(64)), sign, hiShift);
                Operand hiGe = sign;

                res = high ? context.ConditionalSelect(ge32, hiGe, hiShift) : context.ConditionalSelect(ge32, loGe, loPart);
            }

            return context.ConditionalSelect(isZero, high ? hi : lo, res);
        }

        // LEA: (A << shift) + B, shift 75..79; .HI (80): the high word of (C:A) << shift; - on the shifted value (72); .X (74) adds the carry 87..90 and 72 is a bitwise
        // not; the carry out goes to 81..83. SM8x: unverified, the carry out and LEA.HI.SX32 (C is A's sign: the census spells it with C = RZ)
        public static void Sm8xLea(EmitterContext context)
        {
            Sm8xInst inst = I(context);
            bool u = Sm8xHelper.IsUniformAlu(inst);
            (Sm8xSrc bs, Sm8xSrc cs) = Sm8xHelper.SrcBC(context, inst, u);

            Operand a = Sm8xHelper.SrcA(inst, u).Value;
            int shift = inst.Bits(75, 5);
            bool high = inst.Bit(80);
            bool x = inst.Bit(74);

            if (inst.Bit(73))
            {
                Sm8xHelper.Unsupported(context, "LEA .SX32");   // SM8x: unverified bit
            }

            Operand shifted = high
                ? (shift == 0 ? cs.Value : context.BitwiseOr(context.ShiftLeft(cs.Value, Const(shift)), context.ShiftRightU32(a, Const(32 - shift))))
                : context.ShiftLeft(a, Const(shift));

            if (inst.Bit(72))
            {
                shifted = x ? context.BitwiseNot(shifted) : context.INegate(shifted);
            }

            Operand res = context.IAdd(shifted, bs.Value);
            int pd = inst.Bits(81, 3);
            Operand carry = pd != RegisterConsts.PredicateTrueIndex ? context.ICompareLessUnsigned(res, shifted) : null;

            if (x)
            {
                Operand cin = context.ConditionalSelect(Sm8xHelper.Pred(context, inst.SrcPred, inst.SrcPredInv, u), Const(1), Const(0));
                Operand res2 = context.IAdd(res, cin);
                carry = carry != null ? context.BitwiseOr(carry, context.ICompareLessUnsigned(res2, res)) : null;
                res = res2;
            }

            if (carry != null)
            {
                Sm8xHelper.SetPred(context, pd, carry, u);
            }

            Sm8xHelper.SetReg(context, inst.Rd, res, u);
        }

        // PRMT: bytes of C:A (A = bytes 0..3) chosen by the selector B, mode 72..74 (NAK PrmtMode: 0 index, 1 F4E, 2 B4E, 3 RC8, 4 ECL, 5 ECR, 6 RC16; PTX prmt)
        public static void Sm8xPrmt(EmitterContext context)
        {
            Sm8xInst inst = I(context);
            bool u = Sm8xHelper.IsUniformAlu(inst);
            (Sm8xSrc bs, Sm8xSrc cs) = Sm8xHelper.SrcBC(context, inst, u);

            Operand a = Sm8xHelper.SrcA(inst, u).Value;
            Operand sel = bs.Value;
            Operand c = cs.Value;
            int mode = inst.Bits(72, 3);

            Operand Byte(Operand k)   // byte k (0..7) of C:A
            {
                Operand fromA = context.ShiftRightU32(a, context.ShiftLeft(context.BitwiseAnd(k, Const(3)), Const(3)));
                Operand fromC = context.ShiftRightU32(c, context.ShiftLeft(context.BitwiseAnd(k, Const(3)), Const(3)));

                return context.BitwiseAnd(context.ConditionalSelect(context.ICompareLessUnsigned(k, Const(4)), fromA, fromC), Const(0xff));
            }

            Operand s = context.BitwiseAnd(sel, Const(3));
            Operand res = Const(0);

            for (int i = 0; i < 4; i++)
            {
                Operand value;

                switch (mode)
                {
                    case 0:
                        Operand nibble = context.BitfieldExtractU32(sel, Const(i * 4), Const(4));
                        value = Byte(context.BitwiseAnd(nibble, Const(7)));
                        Operand replicate = context.ICompareNotEqual(context.BitwiseAnd(nibble, Const(8)), Const(0));
                        Operand signFill = context.ConditionalSelect(context.ICompareNotEqual(context.BitwiseAnd(value, Const(0x80)), Const(0)), Const(0xff), Const(0));
                        value = context.ConditionalSelect(replicate, signFill, value);
                        break;
                    case 1:
                        value = Byte(context.BitwiseAnd(context.IAdd(s, Const(i)), Const(7)));
                        break;
                    case 2:
                        value = Byte(context.BitwiseAnd(context.ISubtract(s, Const(i)), Const(7)));
                        break;
                    case 3:
                        value = Byte(s);
                        break;
                    case 6:
                        value = Byte(context.IAdd(context.BitwiseAnd(s, Const(2)), Const(i & 1)));
                        break;
                    default:
                        Sm8xHelper.Unsupported(context, $"PRMT mode {mode}");
                        return;
                }

                res = context.BitwiseOr(res, context.ShiftLeft(value, Const(i * 8)));
            }

            Sm8xHelper.SetReg(context, inst.Rd, res, u);
        }

        // I2FP.F32.{U32,S32}: integer to float, signed when 74 (the opcode note's census); rounding 78..79 has no IR counterpart
        public static void Sm8xI2fp(EmitterContext context)
        {
            Sm8xInst inst = I(context);
            Operand src = Sm8xHelper.SrcSecond(context, inst, false).Value;

            Sm8xHelper.SetGpr(context, inst.Rd, inst.Bit(74) ? context.IConvertS32ToFP32(src) : context.IConvertU32ToFP32(src));
        }

        // I2F: source size 84..85 (log2 bytes), signed 74, destination size 75..76 (2 = F32, 1 = F16); a narrow source's byte select at 60..61 (SM8x: unverified)
        public static void Sm8xI2f(EmitterContext context)
        {
            Sm8xInst inst = I(context);
            Operand src = Sm8xHelper.SrcSecond(context, inst, false).Value;
            bool signed = inst.Bit(74);
            int srcBits = 8 << inst.Bits(84, 2);
            int dstSize = inst.Bits(75, 2);

            if (srcBits > 32 || dstSize > 2)
            {
                Sm8xHelper.Unsupported(context, "64-bit I2F");
            }

            if (srcBits < 32)
            {
                Operand offset = Const(inst.Bits(60, 2) * 8);
                src = signed ? context.BitfieldExtractS32(src, offset, Const(srcBits)) : context.BitfieldExtractU32(src, offset, Const(srcBits));
            }

            Operand res = signed ? context.IConvertS32ToFP32(src) : context.IConvertU32ToFP32(src);

            if (dstSize == 1)
            {
                res = context.PackHalf2x16(res, ConstF(0));
            }

            Sm8xHelper.SetGpr(context, inst.Rd, res);
        }

        // F2I: rounding 78..79 (round, floor, ceil, trunc), destination signed 72 and size 75..76, source size 84..85 (1 = F16, its half at 60), clamped to the
        // destination range as the hardware saturates
        public static void Sm8xF2i(EmitterContext context)
        {
            Sm8xInst inst = I(context);
            Sm8xSrc s = Sm8xHelper.SrcSecond(context, inst, false);
            int srcSize = inst.Bits(84, 2);
            int dstBits = 8 << inst.Bits(75, 2);
            bool signed = inst.Bit(72);

            if (srcSize == 3 || dstBits > 32)
            {
                Sm8xHelper.Unsupported(context, "64-bit F2I");
            }

            Operand src = srcSize == 1
                ? (inst.Bit(60) ? context.UnpackHalf2x16High(s.Value) : context.UnpackHalf2x16Low(s.Value))
                : s.Value;

            src = context.FPAbsNeg(src, s.Abs, s.Neg);

            src = (RoundMode2)inst.Bits(78, 2) switch
            {
                RoundMode2.Round => context.FPRound(src),
                RoundMode2.Floor => context.FPFloor(src),
                RoundMode2.Ceil => context.FPCeiling(src),
                _ => context.FPTruncate(src),
            };

            long min = signed ? -(1L << (dstBits - 1)) : 0;
            long max = signed ? (1L << (dstBits - 1)) - 1 : (long)((1UL << dstBits) - 1);

            Operand res;

            if (signed)
            {
                src = context.FPMaximum(src, ConstF(min));
                src = context.FPMinimum(src, ConstF(dstBits == 32 ? 2147483520f : max));
                res = context.FP32ConvertToS32(src);
                res = dstBits == 32 ? context.ConditionalSelect(context.FPCompareLess(ConstF(2147483520f), Sm8xF2iSource(context, inst, s)), Const(int.MaxValue), res) : res;
            }
            else
            {
                src = context.FPMaximum(src, ConstF(0f));
                src = context.FPMinimum(src, ConstF(dstBits == 32 ? 4294967040f : max));
                res = context.FP32ConvertToU32(src);
                res = dstBits == 32 ? context.ConditionalSelect(context.FPCompareLess(ConstF(4294967040f), Sm8xF2iSource(context, inst, s)), Const(-1), res) : res;
            }

            // NaN converts to 0
            Sm8xHelper.SetGpr(context, inst.Rd, context.ConditionalSelect(context.IsNan(src), Const(0), res));
        }

        private static Operand Sm8xF2iSource(EmitterContext context, Sm8xInst inst, Sm8xSrc s)
        {
            Operand v = inst.Bits(84, 2) == 1 ? (inst.Bit(60) ? context.UnpackHalf2x16High(s.Value) : context.UnpackHalf2x16Low(s.Value)) : s.Value;

            return context.FPAbsNeg(v, s.Abs, s.Neg);
        }

        // F2F: between F16 (size 1) and F32 (size 2): destination size 75..76, source size 84..85, the source half 60 (CuAssembler F2F ... H1)
        public static void Sm8xF2f(EmitterContext context)
        {
            Sm8xInst inst = I(context);
            Sm8xSrc s = Sm8xHelper.SrcSecond(context, inst, false);
            int srcSize = inst.Bits(84, 2);
            int dstSize = inst.Bits(75, 2);

            Operand src = srcSize switch
            {
                1 => inst.Bit(60) ? context.UnpackHalf2x16High(s.Value) : context.UnpackHalf2x16Low(s.Value),
                2 => s.Value,
                _ => null,
            };

            if (src == null || dstSize is not 1 and not 2)
            {
                Sm8xHelper.Unsupported(context, $"F2F size {srcSize} -> {dstSize}");
            }

            src = context.FPAbsNeg(src, s.Abs, s.Neg);

            Operand res = dstSize == 1 ? context.PackHalf2x16(src, ConstF(0)) : src;

            Sm8xHelper.SetGpr(context, inst.Rd, res);
        }

        // FRND: round to an integral float, mode 78..79 (round, floor, ceil, trunc), F32 only here
        public static void Sm8xFrnd(EmitterContext context)
        {
            Sm8xInst inst = I(context);

            if (inst.Bits(84, 2) != 2 || inst.Bits(75, 2) != 2)
            {
                Sm8xHelper.Unsupported(context, "FRND other than F32");
            }

            Operand src = Sm8xHelper.F32(context, Sm8xHelper.SrcSecond(context, inst, false));

            Operand res = (RoundMode2)inst.Bits(78, 2) switch
            {
                RoundMode2.Round => context.FPRound(src),
                RoundMode2.Floor => context.FPFloor(src),
                RoundMode2.Ceil => context.FPCeiling(src),
                _ => context.FPTruncate(src),
            };

            Sm8xHelper.SetGpr(context, inst.Rd, res);
        }

        // F2FP.PACK_AB: Rd = half(A) << 16 | half(B) (PTX cvt.rn.f16x2.f32 d, a, b); .MERGE_C (78): the high half from C's half 72 instead (SM8x: unverified);
        // .BF16 (76) not supported
        public static void Sm8xF2fp(EmitterContext context)
        {
            Sm8xInst inst = I(context);

            if (inst.Bit(76))
            {
                Sm8xHelper.Unsupported(context, "F2FP.BF16");
            }

            (Sm8xSrc bs, Sm8xSrc cs) = Sm8xHelper.SrcBC(context, inst, false);
            Operand lo = bs.Value;
            Operand res;

            if (inst.Bit(78))
            {
                Operand hi = inst.Bit(72) ? context.UnpackHalf2x16High(cs.Value) : context.UnpackHalf2x16Low(cs.Value);
                res = context.PackHalf2x16(lo, hi);
            }
            else
            {
                res = context.PackHalf2x16(lo, Sm8xHelper.SrcA(inst, false).Value);
            }

            Sm8xHelper.SetGpr(context, inst.Rd, res);
        }

        // HADD2 / HMUL2: per half; .SAT 77, .F32 78 (the low lane as F32), .FTZ 80; .BF16_V2 85 not supported
        public static void Sm8xHadd2(EmitterContext context) => EmitSm8xHalf2(context, isAdd: true);

        public static void Sm8xHmul2(EmitterContext context) => EmitSm8xHalf2(context, isAdd: false);

        private static void EmitSm8xHalf2(EmitterContext context, bool isAdd)
        {
            Sm8xInst inst = I(context);

            if (inst.Bit(85))
            {
                Sm8xHelper.Unsupported(context, ".BF16_V2");
            }

            Operand[] a = Sm8xHelper.F16x2(context, Sm8xHelper.SrcA(inst, false, fp16: true));
            Operand[] b = Sm8xHelper.F16x2(context, Sm8xHelper.SrcSecond(context, inst, false, fp16: true));
            Operand[] res = new Operand[2];

            for (int i = 0; i < 2; i++)
            {
                res[i] = isAdd ? context.FPAdd(a[i], b[i]) : context.FPMultiply(a[i], b[i]);
                res[i] = context.FPSaturate(res[i], inst.Bit(77));
            }

            Sm8xHelper.SetGpr(context, inst.Rd, inst.Bit(78) ? res[0] : context.PackHalf2x16(res[0], res[1]));
        }

        // HFMA2: per half A * B + C; .SAT 77, .F32 78, .RELU 79 (max with 0)
        public static void Sm8xHfma2(EmitterContext context)
        {
            Sm8xInst inst = I(context);

            if (inst.Bit(85))
            {
                Sm8xHelper.Unsupported(context, ".BF16_V2");
            }

            (Sm8xSrc bs, Sm8xSrc cs) = Sm8xHelper.SrcBC(context, inst, false, fp16: true);
            Operand[] a = Sm8xHelper.F16x2(context, Sm8xHelper.SrcA(inst, false, fp16: true));
            Operand[] b = Sm8xHelper.F16x2(context, bs);
            Operand[] c = Sm8xHelper.F16x2(context, cs);
            Operand[] res = new Operand[2];

            for (int i = 0; i < 2; i++)
            {
                res[i] = context.FPFusedMultiplyAdd(a[i], b[i], c[i]);
                res[i] = context.FPSaturate(res[i], inst.Bit(77));

                if (inst.Bit(79))
                {
                    res[i] = context.FPMaximum(res[i], ConstF(0f));
                }
            }

            Sm8xHelper.SetGpr(context, inst.Rd, inst.Bit(78) ? res[0] : context.PackHalf2x16(res[0], res[1]));
        }

        // HMNMX2: per half, min when the predicate 87..90 is true
        public static void Sm8xHmnmx2(EmitterContext context)
        {
            Sm8xInst inst = I(context);

            if (inst.Bit(85) || inst.Bit(78))
            {
                Sm8xHelper.Unsupported(context, ".BF16_V2 / .F32");
            }

            Operand[] a = Sm8xHelper.F16x2(context, Sm8xHelper.SrcA(inst, false, fp16: true));
            Operand[] b = Sm8xHelper.F16x2(context, Sm8xHelper.SrcSecond(context, inst, false, fp16: true));
            Operand isMin = Sm8xHelper.Pred(context, inst.SrcPred, inst.SrcPredInv);
            Operand[] res = new Operand[2];

            for (int i = 0; i < 2; i++)
            {
                res[i] = context.ConditionalSelect(isMin, context.FPMinimum(a[i], b[i]), context.FPMaximum(a[i], b[i]));
            }

            Sm8xHelper.SetGpr(context, inst.Rd, context.PackHalf2x16(res[0], res[1]));
        }

        // HSETP2: per half comparison 76..79, combined (69..70) with the accumulator; .H_AND (71) ANDs the halves into P(81..83) and its negation into P(84..86)
        public static void Sm8xHsetp2(EmitterContext context)
        {
            Sm8xInst inst = I(context);
            Operand[] a = Sm8xHelper.F16x2(context, Sm8xHelper.SrcA(inst, false, fp16: true));
            Operand[] b = Sm8xHelper.F16x2(context, Sm8xHelper.SrcSecond(context, inst, false, fp16: true));
            FComp cmp = (FComp)inst.Bits(76, 4);
            BoolOp setOp = (BoolOp)inst.Bits(69, 2);
            Operand acc = Sm8xHelper.Pred(context, inst.SrcPred, inst.SrcPredInv);

            Operand lo = GetFPComparison(context, cmp, a[0], b[0]);
            Operand hi = GetFPComparison(context, cmp, a[1], b[1]);
            Operand p0, p1;

            if (inst.Bit(71))
            {
                Operand both = context.BitwiseAnd(lo, hi);
                p0 = InstEmitAluHelper.GetPredLogicalOp(context, setOp, both, acc);
                p1 = InstEmitAluHelper.GetPredLogicalOp(context, setOp, context.BitwiseNot(both), acc);
            }
            else
            {
                p0 = InstEmitAluHelper.GetPredLogicalOp(context, setOp, lo, acc);
                p1 = InstEmitAluHelper.GetPredLogicalOp(context, setOp, hi, acc);
            }

            Sm8xHelper.SetPred(context, inst.Bits(81, 3), p0);
            Sm8xHelper.SetPred(context, inst.Bits(84, 3), p1);
        }

        // IABS: |second source| (NAK OpIAbs)
        public static void Sm8xIabs(EmitterContext context)
        {
            Sm8xInst inst = I(context);

            Sm8xHelper.SetGpr(context, inst.Rd, context.IAbsolute(Sm8xHelper.SrcSecond(context, inst, false).Value));
        }

        // POPC: bit count of the second source, inverted first when 63 (NAK OpPopC)
        public static void Sm8xPopc(EmitterContext context)
        {
            Sm8xInst inst = I(context);
            Operand src = Sm8xHelper.SrcSecond(context, inst, false).Value;

            Sm8xHelper.SetGpr(context, inst.Rd, context.BitCount(context.BitwiseNot(src, inst.Bit(63))));
        }

        // FLO: index of the highest set bit (signed 73: of the highest bit differing from the sign), -1 when none; .SH (74) gives 31 - index.
        // SM8x: unverified bits (NAK OpFlo)
        public static void Sm8xFlo(EmitterContext context)
        {
            Sm8xInst inst = I(context);
            Operand src = context.BitwiseNot(Sm8xHelper.SrcSecond(context, inst, false).Value, inst.Bit(63));
            Operand res = inst.Bit(73) ? context.FindMSBS32(src) : context.FindMSBU32(src);

            if (inst.Bit(74))
            {
                res = context.ConditionalSelect(context.ICompareEqual(res, Const(-1)), Const(-1), context.ISubtract(Const(31), res));
            }

            Sm8xHelper.SetGpr(context, inst.Rd, res);
        }

        // S2R / S2UR: special register 72..79, the numbering Maxwell's S2R uses (CuAssembler's SR_TID.X, SR_CTAID.X, SR_LANEID ... records)
        public static void Sm8xS2r(EmitterContext context)
        {
            Sm8xInst inst = I(context);
            Operand value = GetSReg(context, (SReg)inst.Bits(72, 8));

            if (inst.Opcode == 0x9c3)
            {
                Sm8xHelper.SetUr(context, inst.URd, value);
            }
            else
            {
                Sm8xHelper.SetGpr(context, inst.Rd, value);
            }
        }

        // CS2R: special register 72..79 into Rd (and Rd + 1 with .64, 80); SRZ (0xff) clears the registers, the clocks read as 0 (as Maxwell's S2R)
        public static void Sm8xCs2r(EmitterContext context)
        {
            Sm8xInst inst = I(context);
            int sr = inst.Bits(72, 8);
            Operand value = sr == 0xff ? Const(0) : GetSReg(context, (SReg)sr);

            Sm8xHelper.SetGpr(context, inst.Rd, value);

            if (inst.Bit(80) && inst.Rd != RegisterConsts.RegisterZeroIndex)
            {
                Sm8xHelper.SetGpr(context, inst.Rd + 1, sr == 0xff ? Const(0) : GetSReg(context, (SReg)(sr + 1)));
            }
        }

        // P2R: Rd = (Ra & ~(mask << 8 b)) | ((P0..P6 & mask) << 8 b), mask 32..63, byte b at 76..77; UP2UR (0x883) the same with UR / UP
        public static void Sm8xP2r(EmitterContext context)
        {
            Sm8xInst inst = I(context);
            bool u = inst.Opcode == 0x883;
            int mask = inst.Imm32 & 0x7f;
            int shift = inst.Bits(76, 2) * 8;
            Operand packed = Const(0);

            for (int i = 0; i < 7; i++)
            {
                if ((mask & (1 << i)) != 0)
                {
                    Operand bit = context.ConditionalSelect(Sm8xHelper.Pred(context, i, false, u), Const(1 << i), Const(0));
                    packed = context.BitwiseOr(packed, bit);
                }
            }

            Operand ra = u ? Sm8xHelper.Ur(inst.URa) : Sm8xHelper.Gpr(inst.Ra);
            Operand res = context.BitwiseOr(context.BitwiseAnd(ra, Const(~(mask << shift))), context.ShiftLeft(packed, Const(shift)));

            Sm8xHelper.SetReg(context, inst.Rd, res, u);
        }

        // R2P: P(i) = bit i of (Ra >> 8 b) for each i in the mask 32..63
        public static void Sm8xR2p(EmitterContext context)
        {
            Sm8xInst inst = I(context);
            int mask = inst.Imm32 & 0x7f;
            Operand value = context.ShiftRightU32(Sm8xHelper.Gpr(inst.Ra), Const(inst.Bits(76, 2) * 8));

            for (int i = 0; i < 7; i++)
            {
                if ((mask & (1 << i)) != 0)
                {
                    Sm8xHelper.SetPred(context, i, context.ICompareNotEqual(context.BitwiseAnd(value, Const(1 << i)), Const(0)));
                }
            }
        }

        // VOTE / VOTEU: .ALL / .ANY / .EQ (72..73) of the predicate 87..90 into P(81..83), the ballot into Rd (URd)
        public static void Sm8xVote(EmitterContext context)
        {
            Sm8xInst inst = I(context);
            bool u = inst.Opcode == 0x886;
            Operand pred = Sm8xHelper.Pred(context, inst.SrcPred, inst.SrcPredInv);

            int dest = u ? inst.URd : inst.Rd;

            if (dest != (u ? RegisterConsts.UniformZeroIndex : RegisterConsts.RegisterZeroIndex))
            {
                Sm8xHelper.SetReg(context, dest, EmitBallot(context, pred), u);
            }

            if (inst.Bits(81, 3) != RegisterConsts.PredicateTrueIndex)
            {
                Sm8xHelper.SetPred(context, inst.Bits(81, 3), EmitVote(context, (VoteMode)inst.Bits(72, 2), pred));
            }
        }

        // FSWZADD: A op B per quad lane, the four 2-bit lane operations in 32..39 with lane 0 in the high bits (NAK OpFSwzAdd); Maxwell's FSWZADD keeps lane 0 low
        public static void Sm8xFswzadd(EmitterContext context)
        {
            Sm8xInst inst = I(context);
            int ops = inst.Bits(32, 8);
            int mask = 0;

            for (int lane = 0; lane < 4; lane++)
            {
                mask |= ((ops >> ((3 - lane) * 2)) & 3) << (lane * 2);
            }

            Operand a = Sm8xHelper.Gpr(inst.Ra);
            Operand b = Sm8xHelper.Gpr(inst.Rc);

            Sm8xHelper.SetGpr(context, inst.Rd, context.FPSwizzleAdd(a, b, mask));
        }

        // SHFL: mode 58..59; lane: register 32 (0x389, 0x589) or imm 53..57; clamp / segment mask: register 64 (0x389, 0x989) or imm 40..52; in-bounds to P(81..83)
        public static void Sm8xShfl(EmitterContext context)
        {
            Sm8xInst inst = I(context);
            int op = inst.Opcode;
            Operand lane = op is 0x389 or 0x589 ? Sm8xHelper.Gpr(inst.Rb) : Const(inst.Bits(53, 5));
            Operand c = op is 0x389 or 0x989 ? Sm8xHelper.Gpr(inst.Rc) : Const(inst.Bits(40, 13));
            Operand src = Sm8xHelper.Gpr(inst.Ra);

            (Operand res, Operand valid) = (ShflMode)inst.Bits(58, 2) switch
            {
                ShflMode.Idx => context.Shuffle(src, lane, c),
                ShflMode.Up => context.ShuffleUp(src, lane, c),
                ShflMode.Down => context.ShuffleDown(src, lane, c),
                _ => context.ShuffleXor(src, lane, c),
            };

            Sm8xHelper.SetGpr(context, inst.Rd, res);
            Sm8xHelper.SetPred(context, inst.Bits(81, 3), valid);
        }
    }
}
