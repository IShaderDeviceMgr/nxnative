using Ryujinx.Graphics.Shader.Decoders;
using Ryujinx.Graphics.Shader.Decoders.Sm8x;
using Ryujinx.Graphics.Shader.IntermediateRepresentation;
using Ryujinx.Graphics.Shader.Translation;
using System;
using static Ryujinx.Graphics.Shader.IntermediateRepresentation.OperandHelper;

namespace Ryujinx.Graphics.Shader.Instructions
{
    // nxnative: operand access for SM8x (Volta+) instructions. ALU operand slots after Mesa NAK's encoder (sm70_encode.rs encode_alu_base, MIT licence):
    //   slot A: register at 24..31 (UR at 24..29 for a uniform op), - at 72, |.| at 73 (an F16v2 swizzle at 74..75 instead of a second meaning)
    //   slot 32: register at 32..39 (form 1), imm32 at 32..63 (forms 2, 4), c[54..58][38..53] (forms 3, 5), UR at 32..37 (forms 6, 7); - at 63, |.| at 62, F16v2 swizzle 60..61
    //   slot 64: register at 64..71; - at 75, |.| at 74 (F16v2: - at 84, |.| at 83, swizzle 81..82)
    // Forms 1, 4, 5, 6 put the second source (B) in slot 32 and the third (C) in slot 64; forms 2, 3, 7 swap them, so that imm / c[] / UR is always the third.
    readonly struct Sm8xSrc
    {
        public readonly Operand Value;
        public readonly bool Neg;
        public readonly bool Abs;
        public readonly HalfSwizzle Swizzle;
        public readonly bool IsImm;

        public Sm8xSrc(Operand value, bool neg, bool abs, HalfSwizzle swizzle, bool isImm)
        {
            Value = value;
            Neg = neg;
            Abs = abs;
            Swizzle = swizzle;
            IsImm = isImm;
        }
    }

    static class Sm8xHelper
    {
        public static Sm8xInst Inst(EmitterContext context) => new(context.CurrOp);

        // A uniform datapath ALU op: the 9-bit opcode is the warp op's + 0x80 (NAK encode_ualu), with UR / UP operands
        public static bool IsUniformAlu(Sm8xInst inst) => (inst.Opcode & 0x180) == 0x080;

        public static Operand Gpr(int index) => Register(index, RegisterType.Gpr);

        public static Operand Ur(int index) => index >= RegisterConsts.UniformZeroIndex ? Const(0) : Register(index, RegisterType.Uniform);

        public static Operand Reg(int index, bool uniform) => uniform ? Ur(index) : Gpr(index);

        public static Operand Pred(EmitterContext context, int index, bool not, bool uniform = false)
        {
            Operand p;

            if (index == RegisterConsts.PredicateTrueIndex)
            {
                p = Const(IrConsts.True);
            }
            else
            {
                p = Register(index, uniform ? RegisterType.UniformPredicate : RegisterType.Predicate);
            }

            return not ? context.BitwiseNot(p) : p;
        }

        public static void SetGpr(EmitterContext context, int index, Operand value)
        {
            if (index != RegisterConsts.RegisterZeroIndex)
            {
                context.Copy(Register(index, RegisterType.Gpr), value);
            }
        }

        public static void SetUr(EmitterContext context, int index, Operand value)
        {
            if (index < RegisterConsts.UniformZeroIndex)
            {
                context.Copy(Register(index, RegisterType.Uniform), value);
            }
        }

        public static void SetReg(EmitterContext context, int index, Operand value, bool uniform)
        {
            if (uniform)
            {
                SetUr(context, index, value);
            }
            else
            {
                SetGpr(context, index, value);
            }
        }

        public static void SetPred(EmitterContext context, int index, Operand value, bool uniform = false)
        {
            if (index != RegisterConsts.PredicateTrueIndex)
            {
                context.Copy(Register(index, uniform ? RegisterType.UniformPredicate : RegisterType.Predicate), value);
            }
        }

        public static Sm8xSrc SrcA(Sm8xInst inst, bool uniform, bool fp16 = false)
        {
            return new Sm8xSrc(
                uniform ? Ur(inst.URa) : Gpr(inst.Ra),
                inst.Bit(72),
                inst.Bit(73),
                fp16 ? (HalfSwizzle)inst.Bits(74, 2) : HalfSwizzle.F16,
                false);
        }

        // Slot 32 by form; null when the form puts the slot-64 register second
        private static Sm8xSrc Slot32(EmitterContext context, Sm8xInst inst, bool uniform, bool fp16)
        {
            HalfSwizzle swizzle = fp16 ? (HalfSwizzle)inst.Bits(60, 2) : HalfSwizzle.F16;
            bool neg = inst.Bit(63);
            bool abs = inst.Bit(62);

            switch (inst.Form)
            {
                case 1:
                    return new Sm8xSrc(uniform ? Ur(inst.URb) : Gpr(inst.Rb), neg, abs, swizzle, false);
                case 2:
                case 4:
                    return new Sm8xSrc(Const(inst.Imm32), false, false, HalfSwizzle.F16, true);
                case 3:
                case 5:
                    if (inst.UniformOperand)
                    {
                        throw new NotSupportedException("SM8x bindless constant buffer operand (c[UR][...]) is not supported");   // SM8x: never seen in Pokopia's census
                    }

                    return new Sm8xSrc(Cbuf(inst.CbufSlot, inst.CbufByteOffset >> 2), neg, abs, swizzle, false);
                default:
                    return new Sm8xSrc(Ur(inst.URb), neg, abs, swizzle, false);
            }
        }

        private static Sm8xSrc Slot64(Sm8xInst inst, bool uniform, bool fp16)
        {
            return fp16
                ? new Sm8xSrc(uniform ? Ur(inst.URc) : Gpr(inst.Rc), inst.Bit(84), inst.Bit(83), (HalfSwizzle)inst.Bits(81, 2), false)
                : new Sm8xSrc(uniform ? Ur(inst.URc) : Gpr(inst.Rc), inst.Bit(75), inst.Bit(74), HalfSwizzle.F16, false);
        }

        public static (Sm8xSrc B, Sm8xSrc C) SrcBC(EmitterContext context, Sm8xInst inst, bool uniform, bool fp16 = false)
        {
            Sm8xSrc s32 = Slot32(context, inst, uniform, fp16);
            Sm8xSrc s64 = Slot64(inst, uniform, fp16);

            return inst.Form is 2 or 3 or 7 ? (s64, s32) : (s32, s64);
        }

        // The second operand of a two-source op: B, except in forms 2, 3, 7, whose B is RZ (NAK puts a non-register second source in the third position)
        public static Sm8xSrc SrcSecond(EmitterContext context, Sm8xInst inst, bool uniform, bool fp16 = false)
        {
            (Sm8xSrc b, Sm8xSrc c) = SrcBC(context, inst, uniform, fp16);

            return inst.Form is 2 or 3 or 7 ? c : b;
        }

        public static Operand F32(EmitterContext context, Sm8xSrc src)
        {
            return context.FPAbsNeg(src.Value, src.Abs, src.Neg);
        }

        // Integer source with its - bit as two's complement negation (IADD3, IMAD's C), or as bitwise not (.X forms)
        public static Operand I32(EmitterContext context, Sm8xSrc src, bool not = false)
        {
            if (!src.Neg)
            {
                return src.Value;
            }

            return not ? context.BitwiseNot(src.Value) : context.INegate(src.Value);
        }

        public static Operand[] F16x2(EmitterContext context, Sm8xSrc src)
        {
            Operand[] halves = src.IsImm
                ? InstEmitHelper.GetHalfSrc(context, src.Value.Value)
                : InstEmitHelper.GetHalfUnpacked(context, src.Value, src.Swizzle);

            return InstEmitHelper.FPAbsNeg(context, halves, src.Abs, src.Neg);
        }

        public static void Unsupported(EmitterContext context, string what)
        {
            Sm8xInst inst = Inst(context);

            throw new NotSupportedException($"SM8x {Sm8xTable.GetName(inst.Opcode)} (0x{inst.Opcode:x3}) at 0x{context.CurrOp.Address:x}: {what} not supported");
        }
    }
}
