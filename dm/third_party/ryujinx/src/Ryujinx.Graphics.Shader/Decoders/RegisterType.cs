namespace Ryujinx.Graphics.Shader.Decoders
{
    enum RegisterType
    {
        Flag,
        Gpr,
        Predicate,
        // nxnative: SM8x (Volta+) uniform datapath registers UR0..UR62 and uniform predicates UP0..UP6 (Decoders/Sm8x)
        Uniform,
        UniformPredicate,
    }
}
