using Ryujinx.Graphics.Shader.Decoders;
using Ryujinx.Graphics.Shader.Decoders.Sm8x;
using Ryujinx.Graphics.Shader.IntermediateRepresentation;
using Ryujinx.Graphics.Shader.Translation;
using static Ryujinx.Graphics.Shader.IntermediateRepresentation.OperandHelper;

namespace Ryujinx.Graphics.Shader.Instructions
{
    // nxnative: SM8x constant buffer, attribute, local / shared memory and control flow instructions (fields: Mesa NAK sm70_encode.rs, MIT licence)
    static partial class InstEmit
    {
        // LDC: Rd = c[bank 54..58][Ra + offset 38..53], size 73..75 (Maxwell's LsSize2 numbering: U8 S8 U16 S16 B32 B64), address mode 78..79 (0 only)
        public static void Sm8xLdc(EmitterContext context)
        {
            Sm8xInst inst = Sm8xHelper.Inst(context);
            LsSize2 size = (LsSize2)inst.Bits(73, 3);

            if (inst.Bits(78, 2) != 0 || inst.UniformOperand || size > LsSize2.B64)
            {
                Sm8xHelper.Unsupported(context, $"LDC mode {inst.Bits(78, 2)} size {size}");
            }

            int slot = inst.Bits(54, 5);
            int count = size == LsSize2.B64 ? 2 : 1;

            if (inst.Ra == RegisterConsts.RegisterZeroIndex && size >= LsSize2.B32)
            {
                int word = inst.Bits(38, 16) >> 2;

                for (int index = 0; index < count && inst.Rd + index < RegisterConsts.RegisterZeroIndex; index++)
                {
                    Sm8xHelper.SetGpr(context, inst.Rd + index, Cbuf(slot, word + index));
                }

                return;
            }

            Operand addr = context.IAdd(Sm8xHelper.Gpr(inst.Ra), Const((short)inst.Bits(38, 16)));
            Operand wordOffset = context.ShiftRightU32(addr, Const(2));

            for (int index = 0; index < count && inst.Rd + index < RegisterConsts.RegisterZeroIndex; index++)
            {
                Operand value = EmitLoadConstant(context, Const(slot), context.IAdd(wordOffset, Const(index)));

                if (size < LsSize2.B32)
                {
                    value = ExtractSmallInt(context, (LsSize)size, GetBitOffset(context, addr), value);
                }

                Sm8xHelper.SetGpr(context, inst.Rd + index, value);
            }
        }

        // ULDC: URd = c[bank][offset] (0xab9), or c[bank][URa + signed offset] (0xabb, the opcode note); size 73..75 (B32, B64)
        public static void Sm8xUldc(EmitterContext context)
        {
            Sm8xInst inst = Sm8xHelper.Inst(context);
            LsSize2 size = (LsSize2)inst.Bits(73, 3);

            if (size is not LsSize2.B32 and not LsSize2.B64)
            {
                Sm8xHelper.Unsupported(context, $"ULDC size {size}");
            }

            int slot = inst.Bits(54, 5);
            int count = size == LsSize2.B64 ? 2 : 1;
            bool indexed = inst.Opcode == 0xabb && inst.URa != RegisterConsts.UniformZeroIndex;

            if (!indexed)
            {
                int word = inst.Bits(38, 16) >> 2;

                for (int index = 0; index < count; index++)
                {
                    Sm8xHelper.SetUr(context, inst.URd + index, Cbuf(slot, word + index));
                }

                return;
            }

            Operand addr = context.IAdd(Sm8xHelper.Ur(inst.URa), Const((short)inst.Bits(38, 16)));
            Operand wordOffset = context.ShiftRightU32(addr, Const(2));

            for (int index = 0; index < count; index++)
            {
                Sm8xHelper.SetUr(context, inst.URd + index, EmitLoadConstant(context, Const(slot), context.IAdd(wordOffset, Const(index))));
            }
        }

        // IPA: attribute byte address / 4 at 64..71; the result is Maxwell IPA.PASS's (the shader multiplies by 1 / w itself: Pokopia's pixel shaders read a[0x7c],
        // MUFU.RCP it and FMUL each perspective attribute). Location 76..77 (centroid, offset by Rb) and frequency 78..79 (constant / state) come from the SPH's
        // interpolation map, as on Maxwell. SM8x: unverified, whether a saturate bit exists
        public static void Sm8xIpa(EmitterContext context)
        {
            Sm8xInst inst = Sm8xHelper.Inst(context);

            EmitIpa(context, inst.Rd, inst.Bits(64, 8) << 2, false, inst.Ra, inst.Rb, false, false);
        }

        // ALD: Rd.. = a[Ra + 40..49], components - 1 at 74..75, vertex Rb (32), .P 76, .PHYS 77, .O 79 (NAK OpALd)
        public static void Sm8xAld(EmitterContext context)
        {
            Sm8xInst inst = Sm8xHelper.Inst(context);

            EmitAld(context, inst.Rd, inst.Bits(74, 2) + 1, inst.Bits(40, 10), inst.Ra, inst.Rb, inst.Bit(77), inst.Bit(76), inst.Bit(79));
        }

        // AST: a[Ra + 40..49] = Rb.. (32), components - 1 at 74..75, vertex Rc (64), .P 76, .PHYS 77 (NAK OpASt)
        public static void Sm8xAst(EmitterContext context)
        {
            Sm8xInst inst = Sm8xHelper.Inst(context);

            EmitAst(context, inst.Rb, inst.Bits(74, 2) + 1, inst.Bits(40, 10), inst.Ra, inst.Bit(77), inst.Bit(76));
        }

        // ISBERD: as Maxwell's, the vertex index passes through (the primitive index is 0)
        public static void Sm8xIsberd(EmitterContext context)
        {
            Sm8xInst inst = Sm8xHelper.Inst(context);

            Sm8xHelper.SetGpr(context, inst.Rd, Sm8xHelper.Gpr(inst.Ra));
        }

        // OUT: emit / cut 78..79 (NAK OpOut); Rd receives the next handle (the shader passes it back in Ra)
        public static void Sm8xOut(EmitterContext context)
        {
            Sm8xInst inst = Sm8xHelper.Inst(context);
            int type = inst.Bits(78, 2);

            EmitOut(context, (type & 1) != 0, (type & 2) != 0);
            Sm8xHelper.SetGpr(context, inst.Rd, Sm8xHelper.Gpr(inst.Ra));
        }

        // LDL / LDS: Rd.. = [Ra + UR(32) + signed offset 40..63], size 73..75; STL / STS: [Ra + UR(64, 0x987 / 0x988) + offset] = Rb..
        public static void Sm8xLdl(EmitterContext context) => EmitSm8xLoad(context, StorageKind.LocalMemory);

        public static void Sm8xLds(EmitterContext context) => EmitSm8xLoad(context, StorageKind.SharedMemory);

        public static void Sm8xStl(EmitterContext context) => EmitSm8xStore(context, StorageKind.LocalMemory);

        public static void Sm8xSts(EmitterContext context) => EmitSm8xStore(context, StorageKind.SharedMemory);

        // Ra scaled by the shared memory stride 78..79 (X1, X4, X8, X16: NAK OffsetStride, which applies it to the register only), plus the UR
        private static Operand Sm8xMemAddress(EmitterContext context, Sm8xInst inst, int urStart, int stride)
        {
            Operand addr = Sm8xHelper.Gpr(inst.Ra);

            if (stride != 0)
            {
                addr = context.ShiftLeft(addr, Const(stride + 1));
            }

            if (urStart >= 0 && inst.Bits(urStart, 6) != RegisterConsts.UniformZeroIndex)
            {
                addr = context.IAdd(addr, Sm8xHelper.Ur(inst.Bits(urStart, 6)));
            }

            return addr;
        }

        private static void EmitSm8xLoad(EmitterContext context, StorageKind kind)
        {
            Sm8xInst inst = Sm8xHelper.Inst(context);

            Operand addr = Sm8xMemAddress(context, inst, 32, kind == StorageKind.SharedMemory ? inst.Bits(78, 2) : 0);
            int offset = (inst.Bits(40, 24) << 8) >> 8;

            EmitLoad(context, kind, (LsSize2)inst.Bits(73, 3), addr, inst.Rd, offset);
        }

        private static void EmitSm8xStore(EmitterContext context, StorageKind kind)
        {
            Sm8xInst inst = Sm8xHelper.Inst(context);
            bool hasUr = inst.Opcode is 0x987 or 0x988;

            Operand addr = Sm8xMemAddress(context, inst, hasUr ? 64 : -1, kind == StorageKind.SharedMemory ? inst.Bits(78, 2) : 0);
            int offset = (inst.Bits(40, 24) << 8) >> 8;

            EmitStore(context, kind, (LsSize2)inst.Bits(73, 3), addr, inst.Rb, offset);
        }

        // LDG / STG: the address is Ra (a 64-bit pair when 90 is set, 72 on 0x381 / 0x386) plus, on 0x981 / 0x986, a UR (32 for LDG, 64 for STG; a 64-bit pair when
        // 72 is set) plus the signed offset 40..63; size 73..75; LDG's predicate 64..66 is stored as 7 - P, negated by 67, and a load it disables gives 0 (NAK OpLd /
        // OpSt). SM8x: unverified, the value a disabled load returns
        public static void Sm8xLdg(EmitterContext context)
        {
            Sm8xInst inst = Sm8xHelper.Inst(context);
            bool ur = inst.Opcode == 0x981;
            LsSize size = (LsSize)inst.Bits(73, 3);
            (Operand lo, Operand hi) = Sm8xGlobalAddress(context, inst, ur ? 32 : -1);
            StorageKind kind = GetStorageKind(size);
            Operand pred = null;

            if (ur && (inst.Bits(64, 3) != 0 || inst.Bit(67)))
            {
                pred = Sm8xHelper.Pred(context, 7 - inst.Bits(64, 3), inst.Bit(67));
            }

            for (int index = 0; index < GetVectorCount(size) && inst.Rd + index < RegisterConsts.RegisterZeroIndex; index++)
            {
                Operand value = context.Load(kind, context.IAdd(lo, Const(index * 4)), hi);

                Sm8xHelper.SetGpr(context, inst.Rd + index, pred == null ? value : context.ConditionalSelect(pred, value, Const(0)));
            }
        }

        public static void Sm8xStg(EmitterContext context)
        {
            Sm8xInst inst = Sm8xHelper.Inst(context);
            LsSize size = (LsSize)inst.Bits(73, 3);
            (Operand lo, Operand hi) = Sm8xGlobalAddress(context, inst, inst.Opcode == 0x986 ? 64 : -1);
            StorageKind kind = GetStorageKind(size);

            for (int index = 0; index < GetVectorCount(size); index++)
            {
                int rb = inst.Rb + index >= RegisterConsts.RegisterZeroIndex ? inst.Rb : inst.Rb + index;

                context.Store(kind, context.IAdd(lo, Const(index * 4)), hi, Sm8xHelper.Gpr(rb));
            }
        }

        // ATOMG: the 32-bit integer atomics, as Maxwell's ATOM: Rd = the old value of [Ra (a 64-bit pair when 70 is set, 72 on 0x3a8) + UR(64, 0x9a8 only; a pair when
        // 72 is set) + signed offset 40..63] after Rd = Rd op Rb (32); op 87..90 (AtomOp's numbering, NAK set_atom_op), type 73..75 (0 U32, 1 I32; NAK set_atom_type),
        // the 64-bit and float types and the other operations are rejected. SM8x: unverified, bit 90's meaning (the op field is 87..90 in NAK; the samples set 0)
        public static void Sm8xAtomg(EmitterContext context)
        {
            Sm8xInst inst = Sm8xHelper.Inst(context);
            bool ur = inst.Opcode == 0x9a8;
            AtomOp atomOp = (AtomOp)inst.Bits(87, 4);
            int type = inst.Bits(73, 3);

            if (type > 1 || atomOp is not (AtomOp.Add or AtomOp.Min or AtomOp.Max or AtomOp.And or AtomOp.Or or AtomOp.Xor or AtomOp.Exch))
            {
                Sm8xHelper.Unsupported(context, $"atomic {atomOp} type {type}");
            }

            (Operand lo, Operand hi) = Sm8xGlobalAddress(context, inst, ur ? 64 : -1, ur ? 70 : 72);
            Operand res = EmitAtomicOp(context, StorageKind.GlobalMemory, atomOp, type == 0 ? AtomSize.U32 : AtomSize.S32, lo, hi, Sm8xHelper.Gpr(inst.Rb));

            Sm8xHelper.SetGpr(context, inst.Rd, res);
        }

        private static (Operand Lo, Operand Hi) Sm8xGlobalAddress(EmitterContext context, Sm8xInst inst, int urStart, int wideBit = -1)
        {
            bool wide = inst.Bit(wideBit >= 0 ? wideBit : urStart >= 0 ? 90 : 72);
            Operand lo = context.Copy(Sm8xHelper.Gpr(inst.Ra));
            Operand hi = wide && inst.Ra != RegisterConsts.RegisterZeroIndex ? context.Copy(Sm8xHelper.Gpr(inst.Ra + 1)) : Const(0);

            void Add(Operand addLo, Operand addHi)
            {
                Operand sum = context.IAdd(lo, addLo);
                Operand carry = context.ConditionalSelect(context.ICompareLessUnsigned(sum, addLo), Const(1), Const(0));
                hi = context.IAdd(context.IAdd(hi, addHi), carry);
                lo = sum;
            }

            if (urStart >= 0 && inst.Bits(urStart, 6) != RegisterConsts.UniformZeroIndex)
            {
                int u = inst.Bits(urStart, 6);
                Add(Sm8xHelper.Ur(u), inst.Bit(72) ? Sm8xHelper.Ur(u + 1) : Const(0));
            }

            int offset = (inst.Bits(40, 24) << 8) >> 8;

            if (offset != 0)
            {
                Add(Const(offset), Const(offset < 0 ? -1 : 0));
            }

            return (lo, hi);
        }

        // SUST.P / SULD.P with a bound handle: coordinates from Ra (then the array index, as Maxwell's SUST), data Rb / Rd, dimension 61..63 (Maxwell's SuDim
        // numbering, NAK set_image_dim), components 72..75, handle word 40..53 in the texture handle bank (as TEX: Sm8xTextureHandle)
        public static void Sm8xSust(EmitterContext context)
        {
            Sm8xInst inst = Sm8xHelper.Inst(context);
            int handle = Sm8xTextureHandle(context, inst);

            EmitSust(context, CacheOpSt.Wb, (SuDim)inst.Bits(61, 3), SuSize.B32, handle, (SuRgba)inst.Bits(72, 4), inst.Ra, inst.Rb, 0, useComponents: true, false, isBindless: false);
        }

        public static void Sm8xSuld(EmitterContext context)
        {
            Sm8xInst inst = Sm8xHelper.Inst(context);
            int handle = Sm8xTextureHandle(context, inst);

            EmitSuld(context, CacheOpLd.Ca, (SuDim)inst.Bits(61, 3), SuSize.B32, handle, (SuRgba)inst.Bits(72, 4), inst.Ra, inst.Rd, 0, useComponents: true, false, isBindless: false);
        }

        // BRA / CALL.REL.NOINC: taken when the instruction predicate and the condition 87..90 both hold; the target is the block's last successor (Sm8xDecoder)
        public static void Sm8xBra(EmitterContext context)
        {
            Sm8xInst inst = Sm8xHelper.Inst(context);
            InstOp op = context.CurrOp;
            ulong target = context.CurrBlock.Successors[^1].Address;

            if (target == op.Address + 16)
            {
                return;
            }

            Operand label = context.GetLabel(target);
            Operand cond = Sm8xCondition(context, inst);

            if (cond == null)
            {
                context.Branch(label);
            }
            else
            {
                context.BranchIfTrue(label, cond);
            }
        }

        // The instruction predicate and the condition 87..90 of BRA / EXIT, or null when both are PT
        private static Operand Sm8xCondition(EmitterContext context, Sm8xInst inst)
        {
            Operand cond = null;

            if (inst.Pred != RegisterConsts.PredicateTrueIndex || inst.PredInv)
            {
                cond = Sm8xHelper.Pred(context, inst.Pred, inst.PredInv);
            }

            if (inst.SrcPred != RegisterConsts.PredicateTrueIndex || inst.SrcPredInv)
            {
                Operand p = Sm8xHelper.Pred(context, inst.SrcPred, inst.SrcPredInv);
                cond = cond == null ? p : context.BitwiseAnd(cond, p);
            }

            return cond;
        }

        // EXIT: as Maxwell's, under the instruction predicate and the condition 87..90
        public static void Sm8xExit(EmitterContext context)
        {
            Sm8xInst inst = Sm8xHelper.Inst(context);

            if (context.IsNonMain)
            {
                context.TranslatorContext.GpuAccessor.Log("Invalid exit on non-main function.");
                return;
            }

            Operand cond = Sm8xCondition(context, inst);

            if (cond == null)
            {
                if (context.PrepareForReturn())
                {
                    context.Return();
                }

                return;
            }

            Operand lblSkip = Label();
            context.BranchIfFalse(lblSkip, cond);

            if (context.PrepareForReturn())
            {
                context.Return();
            }

            context.MarkLabel(lblSkip);
        }

        // KILL: discard the fragment, under the instruction predicate and the condition 87..90 (NAK sets it to PT)
        public static void Sm8xKill(EmitterContext context)
        {
            Operand cond = Sm8xCondition(context, Sm8xHelper.Inst(context));

            if (cond == null)
            {
                context.Discard();

                return;
            }

            Operand lblSkip = Label();
            context.BranchIfFalse(lblSkip, cond);
            context.Discard();
            context.MarkLabel(lblSkip);
        }

        // BRX / RET.REL.NODEC: a jump to the program start plus Ra (the base the offset field points at), to one of the targets the decoder found
        // (Sm8xDecoder.FindSm8xIndirectTargets): compared in turn, the last taken without a compare, as Maxwell's BRX does when it cannot chain the blocks
        public static void Sm8xIndirect(EmitterContext context)
        {
            Sm8xInst inst = Sm8xHelper.Inst(context);
            InstOp op = context.CurrOp;
            int startIndex = context.CurrBlock.HasNext() ? 1 : 0;
            int baseAddress = (int)((long)op.Address + 16 + inst.BranchOffset);
            Operand address = context.IAdd(Sm8xHelper.Gpr(inst.Ra), Const(baseAddress));

            var targets = new System.Collections.Generic.SortedSet<ulong>();

            for (int i = startIndex; i < context.CurrBlock.Successors.Count; i++)
            {
                targets.Add(context.CurrBlock.Successors[i].Address);
            }

            int count = 0;

            foreach (ulong target in targets)
            {
                Operand label = context.GetLabel(target);

                if (++count < targets.Count)
                {
                    context.BranchIfTrue(label, context.ICompareEqual(address, Const((int)target)));
                }
                else
                {
                    context.Branch(label);
                }
            }
        }

        public static void Sm8xNop(EmitterContext context)
        {
        }

        // MEMBAR: as Maxwell's MEMBAR
        public static void Sm8xMembar(EmitterContext context)
        {
            context.MemoryBarrier();
        }

        // BAR.SYNC (NAK OpBar encodes only this form)
        public static void Sm8xBar(EmitterContext context)
        {
            context.Barrier();
        }
    }
}
