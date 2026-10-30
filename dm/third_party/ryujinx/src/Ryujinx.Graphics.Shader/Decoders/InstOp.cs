using Ryujinx.Graphics.Shader.Instructions;

namespace Ryujinx.Graphics.Shader.Decoders
{
    readonly struct InstOp
    {
        public readonly ulong Address;
        public readonly ulong RawOpCode;
        public readonly InstEmitter Emitter;
        public readonly InstProps Props;
        public readonly InstName Name;

        // nxnative: an SM8x (Volta+) instruction is 128 bits: RawOpCode holds bits 0..63, RawOpCodeHi bits 64..127 (Decoders/Sm8x)
        public readonly ulong RawOpCodeHi;
        public readonly bool IsSm8x;

        public InstOp(ulong address, ulong rawOpCode, InstName name, InstEmitter emitter, InstProps props)
        {
            Address = address;
            RawOpCode = rawOpCode;
            Name = name;
            Emitter = emitter;
            Props = props;
        }

        public InstOp(ulong address, ulong rawOpCode, ulong rawOpCodeHi, InstEmitter emitter, InstProps props)
        {
            Address = address;
            RawOpCode = rawOpCode;
            RawOpCodeHi = rawOpCodeHi;
            IsSm8x = true;
            Name = InstName.Invalid;
            Emitter = emitter;
            Props = props;
        }

        public ulong GetAbsoluteAddress()
        {
            return (ulong)((long)Address + (((int)(RawOpCode >> 20) << 8) >> 8) + 8);
        }
    }
}
