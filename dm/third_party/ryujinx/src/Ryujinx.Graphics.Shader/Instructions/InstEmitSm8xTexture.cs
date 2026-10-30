using Ryujinx.Graphics.Shader.Decoders;
using Ryujinx.Graphics.Shader.Decoders.Sm8x;
using Ryujinx.Graphics.Shader.IntermediateRepresentation;
using Ryujinx.Graphics.Shader.Translation;
using System;
using System.Collections.Generic;
using System.Numerics;
using static Ryujinx.Graphics.Shader.IntermediateRepresentation.OperandHelper;

namespace Ryujinx.Graphics.Shader.Instructions
{
    // nxnative: SM8x texture instructions with a constant buffer handle (TEX 0xb60, TLD 0xb66, TLD4 0xb63, TXD 0xb6c, TXQ 0xb6f; NAK OpTex / OpTld / OpTld4 / OpTxd /
    // OpTxq with TexRef::CBuf). Fields: handle word offset 40..53 and bank 54..58, Rd 16 and Rd2 64 (two components each: the enabled components of the mask 72..75 in
    // order, the first two to Rd, Rd + 1, the rest to Rd2, Rd2 + 1, as Maxwell's TEXS), sources Ra 24 and Rb 32, .SCR 60, dimension 61..63 (0 1D, 1 2D, 2 3D, 3 cube,
    // 4 1D array, 5 2D array, 7 cube array), .AOFFI 76, .DC 78 (TLD: .MS 78), LOD mode 87..89 (0 auto, 1 LZ, 2 LB, 3 LL, 4 LC, 5 LB.LC), .NODEP 90.
    // The handle is a word offset into the texture handle buffer, as Maxwell's TEX: c[2] in graphics shaders, c[0] in compute shaders (Pokopia's census: no other bank).
    //
    // Source registers: the values in Maxwell TEX's order (array index, coordinates, then LOD / bias, packed offsets, depth reference, sample index) are read from
    // Ra, Ra + 1, ... and then Rb, Rb + 1, .... Without .SCR, Ra holds the array index and the coordinates (Maxwell TEX). With .SCR (TEXS's successor), Ra holds
    // the first 1 (of 1 or 2 values), 2 (of 3 or 4) or 4 (of 5 or more) values. SM8x: unverified, inferred from Pokopia's register allocation (Rb - Ra over 0.5 M
    // instructions: 2D with 2 values has a single-register Ra; 3 and 4 values put 2 in an even-aligned Ra; a cube array with depth reference puts 4 in Ra, 1 in Rb),
    // which matches Maxwell's TEXS layouts; the order of LOD and depth reference when both are present is assumed from Maxwell TEX
    static partial class InstEmit
    {
        private static SamplerType ConvertSm8xDim(EmitterContext context, int dim)
        {
            return dim switch
            {
                0 => SamplerType.Texture1D,
                1 => SamplerType.Texture2D,
                2 => SamplerType.Texture3D,
                3 => SamplerType.TextureCube,
                4 => SamplerType.Texture1D | SamplerType.Array,
                5 => SamplerType.Texture2D | SamplerType.Array,
                7 => SamplerType.TextureCube | SamplerType.Array,
                _ => throw new NotSupportedException($"SM8x texture dimension {dim} at 0x{context.CurrOp.Address:x}"),
            };
        }

        private static int Sm8xTextureHandle(EmitterContext context, Sm8xInst inst)
        {
            int bank = inst.Bits(54, 5);
            int expected = context.TranslatorContext.Definitions.Stage == ShaderStage.Compute ? 0 : 2;

            if (bank != expected)
            {
                Sm8xHelper.Unsupported(context, $"texture handle in c[{bank}]");
            }

            return inst.Bits(40, 14);
        }

        // Reads the source values in order: the first raCount from Ra, the rest from Rb
        private sealed class Sm8xTexSources
        {
            private readonly EmitterContext _context;
            private readonly int _ra;
            private readonly int _rb;
            private readonly int _raCount;
            private int _index;

            public Sm8xTexSources(EmitterContext context, Sm8xInst inst, int count, int raCount)
            {
                _context = context;
                _ra = inst.Ra;
                _rb = inst.Rb;
                _raCount = raCount;
            }

            public static int ScalarSplit(int count) => count <= 2 ? 1 : count <= 4 ? 2 : 4;

            public Operand Next()
            {
                int i = _index++;
                int reg = i < _raCount ? _ra + i : _rb + (i - _raCount);
                int baseReg = i < _raCount ? _ra : _rb;

                if (baseReg == RegisterConsts.RegisterZeroIndex || reg >= RegisterConsts.RegisterZeroIndex)
                {
                    return Const(0);
                }

                return _context.Copy(Register(reg, RegisterType.Gpr));
            }
        }

        private static Operand[] Sm8xTexDests(Sm8xInst inst, int componentMask)
        {
            int count = BitOperations.PopCount((uint)componentMask);
            List<Operand> dests = [];

            for (int i = 0; i < count; i++)
            {
                int baseReg = i < 2 ? inst.Rd : inst.Rc;
                int reg = baseReg + (i & 1);

                // A component whose register is RZ is still sampled into a temporary, so the components after it keep their places
                dests.Add(baseReg == RegisterConsts.RegisterZeroIndex || reg >= RegisterConsts.RegisterZeroIndex ? Local() : Register(reg, RegisterType.Gpr));
            }

            return dests.ToArray();
        }

        public static void Sm8xTex(EmitterContext context) => EmitSm8xTex(context, fetch: false);

        public static void Sm8xTld(EmitterContext context) => EmitSm8xTex(context, fetch: true);

        private static void EmitSm8xTex(EmitterContext context, bool fetch)
        {
            Sm8xInst inst = Sm8xHelper.Inst(context);

            if (inst.Rd == RegisterConsts.RegisterZeroIndex && inst.Rc == RegisterConsts.RegisterZeroIndex)
            {
                return;
            }

            int handle = Sm8xTextureHandle(context, inst);
            SamplerType type = ConvertSm8xDim(context, inst.Bits(61, 3));
            TextureFlags flags = fetch ? TextureFlags.IntCoords : TextureFlags.None;
            int lodMode = inst.Bits(87, 3);
            bool hasOffset = inst.Bit(76);
            bool hasDepthCompare = !fetch && inst.Bit(78);
            bool isMultisample = fetch && inst.Bit(78);
            int componentMask = inst.Bits(72, 4);

            Lod lod = lodMode switch
            {
                0 => 0,
                1 => Lod.Lz,
                2 => Lod.Lb,
                3 => Lod.Ll,
                _ => (Lod)(-1),
            };

            if ((int)lod < 0 || (fetch && lod is not Lod.Lz and not Lod.Ll))
            {
                Sm8xHelper.Unsupported(context, $"LOD mode {lodMode}");
            }

            bool isArray = type.HasFlag(SamplerType.Array);
            bool hasLod = lod > Lod.Lz;
            int coordsCount = type.Dimensions;
            int count = (isArray ? 1 : 0) + coordsCount + (hasLod ? 1 : 0) + (hasOffset ? 1 : 0) + (hasDepthCompare ? 1 : 0) + (isMultisample ? 1 : 0);
            Sm8xTexSources src = new(context, inst, count, inst.Bit(60) ? Sm8xTexSources.ScalarSplit(count) : (isArray ? 1 : 0) + coordsCount);

            Operand arrayIndex = isArray ? src.Next() : null;
            List<Operand> sourcesList = [];
            bool wrap1D = false;

            if (type == SamplerType.Texture1D && flags == TextureFlags.IntCoords && !(hasLod || hasOffset || isMultisample))
            {
                // As Maxwell TLD (EmitTex): the descriptor decides between a 1D texture and a texture buffer
                if (context.TranslatorContext.GpuAccessor.QuerySamplerType(handle) == SamplerType.TextureBuffer)
                {
                    type = SamplerType.TextureBuffer;
                }
                else
                {
                    wrap1D = Sample1DAs2D;
                }
            }

            for (int index = 0; index < coordsCount; index++)
            {
                sourcesList.Add(src.Next());
            }

            bool is1DTo2D = false;

            if (Sample1DAs2D && (type & SamplerType.Mask) == SamplerType.Texture1D)
            {
                if (wrap1D)
                {
                    Operand x = sourcesList[^1];
                    sourcesList[^1] = context.BitwiseAnd(x, Const(0x3FFF));
                    sourcesList.Add(context.ShiftRightU32(x, Const(14)));
                    context.TranslatorContext.GpuAccessor.RecordTexelFetch1D(handle);
                }
                else
                {
                    sourcesList.Add(fetch ? Const(0) : ConstF(0));
                }

                type = SamplerType.Texture2D | (type & SamplerType.Array);
                is1DTo2D = true;
            }

            if (isArray)
            {
                sourcesList.Add(arrayIndex);
            }

            Operand lodValue = hasLod ? src.Next() : (fetch ? Const(0) : ConstF(0));
            Operand packedOffs = hasOffset ? src.Next() : null;

            if (hasDepthCompare)
            {
                sourcesList.Add(src.Next());

                type |= SamplerType.Shadow;
            }

            if ((lod == Lod.Lz || lod == Lod.Ll) && !isMultisample && type != SamplerType.TextureBuffer)
            {
                sourcesList.Add(lodValue);

                flags |= TextureFlags.LodLevel;
            }

            if (hasOffset)
            {
                for (int index = 0; index < coordsCount; index++)
                {
                    sourcesList.Add(context.BitfieldExtractS32(packedOffs, Const(index * 4), Const(4)));
                }

                if (is1DTo2D)
                {
                    sourcesList.Add(Const(0));
                }

                flags |= TextureFlags.Offset;
            }

            if (lod == Lod.Lb)
            {
                sourcesList.Add(lodValue);

                flags |= TextureFlags.LodBias;
            }

            if (isMultisample)
            {
                sourcesList.Add(src.Next());

                type |= SamplerType.Multisample;
            }

            EmitTextureSample(context, type, flags, handle, componentMask, Sm8xTexDests(inst, componentMask), sourcesList.ToArray());
        }

        // TMML (0xb69): the texture LOD query, as Maxwell's TMML (EmitTmml): the sources are the array index and coordinates (Ra, then Rb), the components of the mask 72..75 go
        // to Rd, Rd + 1 (then Rc, Rc + 1) in order, each the LOD of GLSL's textureQueryLod component (instruction component ^ 1) in 24.8 fixed point; z and w are 0.
        // SM8x: unverified, that the instruction's components keep Maxwell's order (the samples read only component 1) and that .NDV (77) / .SCR (60) need no handling
        public static void Sm8xTmml(EmitterContext context)
        {
            Sm8xInst inst = Sm8xHelper.Inst(context);

            if (inst.Rd == RegisterConsts.RegisterZeroIndex && inst.Rc == RegisterConsts.RegisterZeroIndex)
            {
                return;
            }

            if (inst.Bit(60))
            {
                Sm8xHelper.Unsupported(context, "TMML.SCR");
            }

            int handle = Sm8xTextureHandle(context, inst);
            SamplerType type = ConvertSm8xDim(context, inst.Bits(61, 3));
            int componentMask = inst.Bits(72, 4);
            bool isArray = type.HasFlag(SamplerType.Array);
            int coordsCount = type.Dimensions;
            Sm8xTexSources src = new(context, inst, (isArray ? 1 : 0) + coordsCount, (isArray ? 1 : 0) + coordsCount);

            Operand arrayIndex = isArray ? src.Next() : null;
            List<Operand> sourcesList = [];

            for (int index = 0; index < coordsCount; index++)
            {
                sourcesList.Add(src.Next());
            }

            if (Sample1DAs2D && (type & SamplerType.Mask) == SamplerType.Texture1D)
            {
                sourcesList.Add(ConstF(0));
                type = SamplerType.Texture2D | (type & SamplerType.Array);
            }

            if (isArray)
            {
                sourcesList.Add(arrayIndex);
            }

            Operand[] sources = sourcesList.ToArray();
            Operand[] dests = Sm8xTexDests(inst, componentMask);
            SetBindingPair binding = context.ResourceManager.GetTextureOrImageBinding(
                Instruction.Lod, type, TextureFormat.Unknown, TextureFlags.None, TextureOperation.DefaultCbufSlot, handle);
            int outIndex = 0;

            for (int comp = 0; comp < 4; comp++)
            {
                if ((componentMask & (1 << comp)) == 0)
                {
                    continue;
                }

                if (comp >= 2)
                {
                    context.Copy(dests[outIndex++], Const(0));
                }
                else
                {
                    Operand res = context.Lod(type, TextureFlags.None, binding, comp ^ 1, sources);

                    context.Copy(dests[outIndex++], context.FP32ConvertToS32(context.FPMultiply(res, ConstF(256.0f))));
                }
            }
        }

        // TLD4: component 87..88, offsets 76..77 (1 AOFFI, 2 PTP: two registers), .DC 78; values in Maxwell TLD4's order
        public static void Sm8xTld4(EmitterContext context)
        {
            Sm8xInst inst = Sm8xHelper.Inst(context);

            if (inst.Rd == RegisterConsts.RegisterZeroIndex && inst.Rc == RegisterConsts.RegisterZeroIndex)
            {
                return;
            }

            int handle = Sm8xTextureHandle(context, inst);
            SamplerType type = ConvertSm8xDim(context, inst.Bits(61, 3));
            int offsetMode = inst.Bits(76, 2);
            bool hasDepthCompare = inst.Bit(78);
            int componentMask = inst.Bits(72, 4);
            TextureFlags flags = TextureFlags.Gather;

            bool isArray = type.HasFlag(SamplerType.Array);
            int coordsCount = type.Dimensions;
            int offsetRegs = offsetMode switch { 1 => 1, 2 => 2, _ => 0 };
            int count = (isArray ? 1 : 0) + coordsCount + offsetRegs + (hasDepthCompare ? 1 : 0);
            Sm8xTexSources src = new(context, inst, count, inst.Bit(60) ? Sm8xTexSources.ScalarSplit(count) : (isArray ? 1 : 0) + coordsCount);

            Operand arrayIndex = isArray ? src.Next() : null;
            List<Operand> sourcesList = [];

            for (int index = 0; index < coordsCount; index++)
            {
                sourcesList.Add(src.Next());
            }

            bool is1DTo2D = Sample1DAs2D && (type & SamplerType.Mask) == SamplerType.Texture1D;

            if (is1DTo2D)
            {
                sourcesList.Add(ConstF(0));
                type = SamplerType.Texture2D | (type & SamplerType.Array);
            }

            if (isArray)
            {
                sourcesList.Add(arrayIndex);
            }

            Operand[] packedOffs = new Operand[2];
            packedOffs[0] = offsetRegs > 0 ? src.Next() : null;
            packedOffs[1] = offsetRegs > 1 ? src.Next() : null;

            if (hasDepthCompare)
            {
                sourcesList.Add(src.Next());
                type |= SamplerType.Shadow;
            }

            if (offsetRegs > 0)
            {
                int offsetTexelsCount = offsetRegs == 2 ? 4 : 1;

                for (int index = 0; index < coordsCount * offsetTexelsCount; index++)
                {
                    Operand packed = packedOffs[(index >> 2) & 1];

                    sourcesList.Add(context.BitfieldExtractS32(packed, Const((index & 3) * 8), Const(6)));
                }

                if (is1DTo2D)
                {
                    for (int index = 0; index < offsetTexelsCount; index++)
                    {
                        sourcesList.Add(Const(0));
                    }
                }

                flags |= offsetRegs == 2 ? TextureFlags.Offsets : TextureFlags.Offset;
            }

            if (!hasDepthCompare)
            {
                sourcesList.Add(Const(inst.Bits(87, 2)));
            }

            EmitTextureSample(context, type, flags, handle, componentMask, Sm8xTexDests(inst, componentMask), sourcesList.ToArray());
        }

        // TXD: Ra = array index, coordinates, packed offsets (AOFFI 76); Rb = the derivatives, x and y of each coordinate in turn (NAK's lowering; SM8x: unverified)
        public static void Sm8xTxd(EmitterContext context)
        {
            Sm8xInst inst = Sm8xHelper.Inst(context);

            if (inst.Rd == RegisterConsts.RegisterZeroIndex && inst.Rc == RegisterConsts.RegisterZeroIndex)
            {
                return;
            }

            int handle = Sm8xTextureHandle(context, inst);
            SamplerType type = ConvertSm8xDim(context, inst.Bits(61, 3));
            bool hasOffset = inst.Bit(76);
            int componentMask = inst.Bits(72, 4);
            TextureFlags flags = TextureFlags.Derivatives;

            bool isArray = type.HasFlag(SamplerType.Array);
            int coordsCount = type.Dimensions;
            int raCount = (isArray ? 1 : 0) + coordsCount + (hasOffset ? 1 : 0);
            Sm8xTexSources src = new(context, inst, raCount + 2 * coordsCount, raCount);

            Operand arrayIndex = isArray ? src.Next() : null;
            List<Operand> sourcesList = [];

            for (int index = 0; index < coordsCount; index++)
            {
                sourcesList.Add(src.Next());
            }

            bool is1DTo2D = Sample1DAs2D && (type & SamplerType.Mask) == SamplerType.Texture1D;

            if (is1DTo2D)
            {
                sourcesList.Add(ConstF(0));
                type = SamplerType.Texture2D | (type & SamplerType.Array);
            }

            if (isArray)
            {
                sourcesList.Add(arrayIndex);
            }

            Operand packedOffs = hasOffset ? src.Next() : null;

            for (int dIndex = 0; dIndex < 2 * coordsCount; dIndex++)
            {
                sourcesList.Add(src.Next());

                if (is1DTo2D)
                {
                    sourcesList.Add(ConstF(0));
                }
            }

            if (hasOffset)
            {
                for (int index = 0; index < coordsCount; index++)
                {
                    sourcesList.Add(context.BitfieldExtractS32(packedOffs, Const(index * 4), Const(4)));
                }

                if (is1DTo2D)
                {
                    sourcesList.Add(Const(0));
                }

                flags |= TextureFlags.Offset;
            }

            EmitTextureSample(context, type, flags, handle, componentMask, Sm8xTexDests(inst, componentMask), sourcesList.ToArray());
        }

        // TXQ: query 62..63 (0 dimensions, Ra = LOD; 1 texture type: the sample count in component 2), components 72..75 into Rd, Rd + 1, Rd2, Rd2 + 1
        public static void Sm8xTxq(EmitterContext context)
        {
            Sm8xInst inst = Sm8xHelper.Inst(context);
            int handle = Sm8xTextureHandle(context, inst);
            int query = inst.Bits(62, 2);
            int componentMask = inst.Bits(72, 4);
            Operand[] dests = Sm8xTexDests(inst, componentMask);
            Operand[] sources = [inst.Ra == RegisterConsts.RegisterZeroIndex ? Const(0) : context.Copy(Register(inst.Ra, RegisterType.Gpr))];
            SamplerType type = context.TranslatorContext.GpuAccessor.QuerySamplerType(handle);
            int outIndex = 0;

            if (query == 0)
            {
                SetBindingPair binding = context.ResourceManager.GetTextureOrImageBinding(
                    Instruction.TextureQuerySize, type, TextureFormat.Unknown, TextureFlags.None, TextureOperation.DefaultCbufSlot, handle);

                for (int comp = 0; comp < 4; comp++)
                {
                    if ((componentMask & (1 << comp)) != 0)
                    {
                        context.Copy(dests[outIndex++], context.TextureQuerySize(type, TextureFlags.None, binding, comp, sources));
                    }
                }
            }
            else if (query == 1)
            {
                SetBindingPair binding = context.ResourceManager.GetTextureOrImageBinding(
                    Instruction.TextureQuerySamples, type, TextureFormat.Unknown, TextureFlags.None, TextureOperation.DefaultCbufSlot, handle);

                for (int comp = 0; comp < 4; comp++)
                {
                    if ((componentMask & (1 << comp)) != 0)
                    {
                        // SM8x: unverified, the texture type query's other components (Maxwell's: only the sample count is read)
                        context.Copy(dests[outIndex++], comp == 2 ? context.TextureQuerySamples(type, TextureFlags.None, binding, sources) : Const(0));
                    }
                }
            }
            else
            {
                Sm8xHelper.Unsupported(context, $"TXQ query {query}");
            }
        }
    }
}
