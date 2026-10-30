namespace Ryujinx.Graphics.Shader.Decoders
{
    static class RegisterConsts
    {
        public const int GprsCount = 255;
        public const int PredsCount = 7;
        public const int FlagsCount = 4;
        public const int TotalCount = GprsCount + PredsCount + FlagsCount;

        public const int RegisterZeroIndex = GprsCount;
        public const int PredicateTrueIndex = PredsCount;

        // nxnative: SM8x uniform registers (URZ = 63) and uniform predicates (UPT = 7), after the Maxwell register files in SSA's numbering
        public const int UniformCount = 63;
        public const int UniformPredsCount = 7;
        public const int UniformZeroIndex = UniformCount;
        public const int UniformPredicateTrueIndex = UniformPredsCount;
        public const int AllCount = TotalCount + UniformCount + UniformPredsCount;
    }
}
