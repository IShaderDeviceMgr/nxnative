using System;

namespace Ryujinx.Graphics.Shader.Translation
{
    [Flags]
    public enum TranslationFlags
    {
        None = 0,

        VertexA = 1 << 0,
        Compute = 1 << 1,
        DebugMode = 1 << 2,
        // Offline Metal: image atomics run natively on the texture (MSL 3.1), guarded for IGN bounds.
        MetalImageAtomics = 1 << 3,
        // nxnative: the code is SM8x (Volta+ 128-bit instructions, Switch 2): graphics code starts after a 0x80-byte SPH (version 4), see Decoders/Sm8x
        Sm8x = 1 << 4,
    }
}
