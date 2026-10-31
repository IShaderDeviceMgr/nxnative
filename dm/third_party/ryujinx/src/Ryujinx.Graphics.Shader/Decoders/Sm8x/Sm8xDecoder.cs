using Ryujinx.Graphics.Shader.Decoders.Sm8x;
using Ryujinx.Graphics.Shader.Translation;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Ryujinx.Graphics.Shader.Decoders
{
    // nxnative: the control flow graph of an SM8x (Volta+ 128-bit instruction) program, for the same DecodedProgram the Maxwell decoder fills.
    // Volta replaced Maxwell's SSY/SYNC/PBK/BRK stack, whose SYNC and BRK are jumps, with convergence barriers: BSSY records where a warp reconverges and BSYNC waits
    // there, while every change of a thread's path is an explicit BRA (or CALL.REL.NOINC / EXIT). The graph is therefore built from those alone, as for a scalar CPU;
    // the structurizer after SSA rebuilds the loops and conditionals. One function: Pokopia's CALL.REL.NOINC pushes no return address and its RET.REL.NODEC returns
    // through a register (see Sm8xTable), so both are jumps.
    static partial class Decoder
    {
        private const int Sm8xInstSize = 16;

        public static DecodedProgram DecodeSm8x(ShaderDefinitions definitions, IGpuAccessor gpuAccessor, ulong startAddress)
        {
            Context context = new(gpuAccessor);
            Dictionary<ulong, InstOp> ops = new();
            SortedSet<ulong> leaders = [0];
            Queue<ulong> queue = new();
            Dictionary<ulong, SortedSet<ulong>> indirectTargets = new();
            queue.Enqueue(0);

            while (true)
            {
                while (queue.TryDequeue(out ulong address))
                {
                    while (!ops.ContainsKey(address))
                    {
                        ReadOnlySpan<ulong> code = gpuAccessor.GetCode(startAddress + address, Sm8xInstSize);

                        if (code.Length < 2)
                        {
                            throw new InvalidOperationException($"SM8x code ends inside the program at 0x{address:x}");
                        }

                        Sm8xInst inst = new(code[0], code[1]);
                        Sm8xTable.Entry entry = Sm8xTable.Get(inst.Opcode);
                        ops.Add(address, new InstOp(address, code[0], code[1], entry.Emitter, InstProps.None));

                        if (!Sm8xTable.IsNever(inst))
                        {
                            RecordSm8xUses(definitions, context, inst, entry.Kind);
                        }

                        ulong next = address + Sm8xInstSize;

                        if (Sm8xTable.IsTerminator(inst, out Sm8xKind kind))
                        {
                            if (kind == Sm8xKind.Indirect)
                            {
                                indirectTargets.TryAdd(address, []);
                            }

                            if (kind == Sm8xKind.Branch)
                            {
                                ulong target = (ulong)((long)next + inst.BranchOffset);

                                if (target % Sm8xInstSize != 0)
                                {
                                    throw new NotSupportedException($"SM8x {Sm8xTable.GetName(inst.Opcode)} at 0x{address:x}: target 0x{target:x} is not an instruction address");
                                }

                                leaders.Add(target);
                                queue.Enqueue(target);
                            }

                            leaders.Add(next);

                            if (!Sm8xTable.IsConditional(inst, kind))
                            {
                                break;
                            }
                        }

                        address = next;
                    }
                }

                // Indirect jumps: their targets come from the code decoded so far, which they may extend; repeat until no new target appears
                bool grew = false;
                Dictionary<ulong, List<ulong>> predecessors = BuildSm8xPredecessors(ops, indirectTargets);

                foreach ((ulong address, SortedSet<ulong> targets) in indirectTargets)
                {
                    foreach (ulong target in FindSm8xIndirectTargets(context, ops, predecessors, address))
                    {
                        // A table entry that is not an instruction address means the pattern or the table is not what it was taken for: never jump into an instruction
                        if (target % Sm8xInstSize != 0 || gpuAccessor.GetCode(startAddress + target, Sm8xInstSize).Length < 2)
                        {
                            throw new NotSupportedException($"SM8x indirect branch {Sm8xTable.GetName(new Sm8xInst(ops[address]).Opcode)} at 0x{address:x}: target 0x{target:x} is not an instruction address");
                        }

                        if (targets.Add(target))
                        {
                            grew = true;
                            leaders.Add(target);
                            queue.Enqueue(target);
                        }
                    }
                }

                if (!grew)
                {
                    break;
                }
            }

            foreach ((ulong address, SortedSet<ulong> targets) in indirectTargets)
            {
                if (targets.Count == 0)
                {
                    throw new NotSupportedException($"SM8x indirect branch {Sm8xTable.GetName(new Sm8xInst(ops[address]).Opcode)} at 0x{address:x}: targets not found");
                }
            }

            // Blocks: from each leader to the next leader or terminator, over the reachable instructions only
            List<Block> blocks = [];
            Dictionary<ulong, Block> byAddress = new();
            Block current = null;

            foreach (ulong address in ops.Keys.Order())
            {
                if (current == null || leaders.Contains(address) || current.EndAddress != address)
                {
                    current = new Block(address);
                    blocks.Add(current);
                    byAddress.Add(address, current);
                }

                current.OpCodes.Add(ops[address]);
                current.EndAddress = address + Sm8xInstSize;
            }

            void Link(Block from, ulong to, bool first)
            {
                if (!byAddress.TryGetValue(to, out Block target))
                {
                    throw new InvalidOperationException($"SM8x branch from 0x{from.Address:x} to 0x{to:x}, which is not an instruction start");
                }

                if (first)
                {
                    from.Successors.Insert(0, target);
                }
                else
                {
                    from.Successors.Add(target);
                }

                target.Predecessors.Add(from);
            }

            foreach (Block block in blocks)
            {
                InstOp last = block.GetLastOp();
                Sm8xInst inst = new(last);

                if (Sm8xTable.IsTerminator(inst, out Sm8xKind kind))
                {
                    if (kind == Sm8xKind.Branch)
                    {
                        Link(block, (ulong)((long)block.EndAddress + inst.BranchOffset), false);
                    }
                    else if (kind == Sm8xKind.Indirect)
                    {
                        foreach (ulong target in indirectTargets[last.Address])
                        {
                            Link(block, target, false);
                        }
                    }

                    if (Sm8xTable.IsConditional(inst, kind))
                    {
                        Link(block, block.EndAddress, true);
                    }
                }
                else if (byAddress.ContainsKey(block.EndAddress))
                {
                    Link(block, block.EndAddress, true);
                }
            }

            DecodedFunction main = new(0);
            main.SetBlocks(blocks.ToArray());

            return new DecodedProgram(
                main,
                new Dictionary<ulong, DecodedFunction> { [0] = main },
                context.AttributeUsage,
                context.UsedFeatures,
                context.ClipDistancesWritten,
                context.Cb1DataSize);
        }

        // The possible targets of an indirect jump (all relative to the program start, the base its offset field points at: the opcode note's 3,963 of 3,968 BRX).
        //  - BRX R: Maxwell's jump table pattern, found the same way (FindBrxTargetRange): IMNMX.U32 Rk, Rj, bound, PT; SHF.L.U32 Ri, Rk, 2, RZ (or IMAD.SHL by 4);
        //    LDC R, c[1][Ri + base] (the table of bound + 1 offsets in c1, read through ConstantBuffer1Read)
        //  - RET.REL.NODEC R: the return points, the instructions after each CALL.REL.NOINC whose offset the program loads with MOV imm before the call
        //    (SM8x: unverified that a return never goes elsewhere; Pokopia's compiler sets the return offset only this way)
        private static IEnumerable<ulong> FindSm8xIndirectTargets(Context context, Dictionary<ulong, InstOp> ops, Dictionary<ulong, List<ulong>> predecessors, ulong address)
        {
            Sm8xInst inst = new(ops[address]);
            long baseAddress = (long)address + Sm8xInstSize + inst.BranchOffset;

            if (inst.Opcode == 0x950)
            {
                // The return offset reaches RET's register through copies inside the callee (Pokopia: MOV R32, 0x1430; CALL; ... RET R26), so the register is
                // not traced: a target is the instruction after a CALL.REL.NOINC whose offset some MOV imm writes (a CALL used as a jump has none)
                HashSet<long> movImmediates = [];

                foreach (InstOp op in ops.Values)
                {
                    Sm8xInst mov = new(op);

                    if (mov.Opcode == 0x802 && !Sm8xTable.IsNever(mov))
                    {
                        movImmediates.Add(baseAddress + (uint)mov.Imm32);
                    }
                }

                foreach (InstOp op in ops.Values)
                {
                    if (new Sm8xInst(op).Opcode == 0x944 && movImmediates.Contains((long)op.Address + Sm8xInstSize))
                    {
                        yield return op.Address + Sm8xInstSize;
                    }
                }

                yield break;
            }

            if (inst.Opcode == 0x958)
            {
                // BRXU UR: the uniform form, ULDC URt, c[1][URi + base] (0xabb); USHF.L.U32 URi, URk, 2, URZ; USEL URk, URj, bound, UP (Pokopia clamps the index
                // with UISETP + USEL instead of IMNMX)
                if (!TryFindSm8xWriter(ops, predecessors, address, inst.URa, true, out Sm8xInst uldc, out ulong uldcAddress) || uldc.Opcode != 0xabb || uldc.Bits(54, 5) != 1 || uldc.Bits(73, 3) != 4 ||
                    !TryFindSm8xWriter(ops, predecessors, uldcAddress, uldc.URa, true, out Sm8xInst ushf, out ulong ushfAddress) ||
                    ushf.Opcode != 0x899 || ushf.Imm32 != 2 || ushf.URc != RegisterConsts.UniformZeroIndex || ushf.Bit(76) || ushf.Bits(73, 2) != 3 ||
                    !TryFindSm8xWriter(ops, predecessors, ushfAddress, ushf.URa, true, out Sm8xInst usel, out _) || usel.Opcode != 0x887)
                {
                    yield break;
                }

                int ucbBase = (short)uldc.Bits(38, 16);

                for (int i = 0; i <= usel.Imm32 && i < 4096; i++)
                {
                    yield return (ulong)(baseAddress + context.ConstantBuffer1Read(ucbBase + i * 4));
                }

                yield break;
            }

            if (inst.Opcode != 0x949)
            {
                yield break;
            }

            if (!TryFindSm8xWriter(ops, predecessors, address, inst.Ra, false, out Sm8xInst ldc, out ulong ldcAddress) || ldc.Opcode != 0xb82 || ldc.Bits(54, 5) != 1 || ldc.Bits(73, 3) != 4 || ldc.Bits(78, 2) != 0)
            {
                yield break;
            }

            if (!TryFindSm8xWriter(ops, predecessors, ldcAddress, ldc.Ra, false, out Sm8xInst shl, out ulong shlAddress))
            {
                yield break;
            }

            bool isShl = (shl.Opcode == 0x819 && shl.Imm32 == 2 && shl.Rc == RegisterConsts.RegisterZeroIndex && !shl.Bit(76) && shl.Bits(73, 2) == 3) ||
                         (shl.Opcode == 0x824 && shl.Imm32 == 4 && shl.Rc == RegisterConsts.RegisterZeroIndex);

            if (!isShl || !TryFindSm8xWriter(ops, predecessors, shlAddress, shl.Ra, false, out Sm8xInst imnmx, out _))
            {
                yield break;
            }

            if (imnmx.Opcode != 0x817 || imnmx.Bit(73) || imnmx.SrcPred != RegisterConsts.PredicateTrueIndex || imnmx.SrcPredInv)
            {
                yield break;
            }

            int cbBase = (short)ldc.Bits(38, 16);

            for (int i = 0; i <= imnmx.Imm32 && i < 4096; i++)
            {
                yield return (ulong)(baseAddress + context.ConstantBuffer1Read(cbBase + i * 4));
            }
        }

        // The control flow predecessors of each decoded instruction: the instruction before it unless that one always jumps away, every branch (BRA, CALL.REL.NOINC)
        // whose offset field lands on it, and every indirect jump (BRX, BRXU, RET) whose targets found so far include it. BSSY / BSYNC / BREAK jump nowhere (see Sm8xTable)
        private static Dictionary<ulong, List<ulong>> BuildSm8xPredecessors(Dictionary<ulong, InstOp> ops, Dictionary<ulong, SortedSet<ulong>> indirectTargets)
        {
            Dictionary<ulong, List<ulong>> predecessors = new();

            void Add(ulong to, ulong from)
            {
                if (!predecessors.TryGetValue(to, out List<ulong> list))
                {
                    predecessors.Add(to, list = []);
                }

                list.Add(from);
            }

            foreach ((ulong address, InstOp op) in ops)
            {
                Sm8xInst inst = new(op);

                if (!Sm8xTable.IsTerminator(inst, out Sm8xKind kind))
                {
                    Add(address + Sm8xInstSize, address);
                    continue;
                }

                if (kind == Sm8xKind.Branch)
                {
                    Add((ulong)((long)address + Sm8xInstSize + inst.BranchOffset), address);
                }

                if (Sm8xTable.IsConditional(inst, kind))
                {
                    Add(address + Sm8xInstSize, address);
                }
            }

            foreach ((ulong address, SortedSet<ulong> targets) in indirectTargets)
            {
                foreach (ulong target in targets)
                {
                    Add(target, address);
                }
            }

            return predecessors;
        }

        // The instruction that wrote reg (a UR when uniform) as it is read at address, found as a reaching definition over the control flow graph, backwards from address:
        // the same instruction on every path (the clamp of a jump table sits before the branch that skips part of the shader, and the BRX behind it: Bananza's pixel shaders
        // run 24..49 instructions from the table load across a conditional BRA). A path that reaches the program start without a write (the register is an input) or a second
        // writer on another path fails, as does a search that visits more than 256 instructions; a predicated writer ends its path as a write, as for any straight-line code
        private static bool TryFindSm8xWriter(Dictionary<ulong, InstOp> ops, Dictionary<ulong, List<ulong>> predecessors, ulong address, int reg, bool uniform, out Sm8xInst writer, out ulong at)
        {
            writer = default;
            at = 0;

            HashSet<ulong> visited = [address];
            Stack<ulong> pending = new();
            pending.Push(address);
            bool found = false;

            while (pending.TryPop(out ulong current))
            {
                if (!predecessors.TryGetValue(current, out List<ulong> list) || list.Count == 0)
                {
                    return false;
                }

                foreach (ulong p in list)
                {
                    if (!ops.TryGetValue(p, out InstOp op))
                    {
                        return false;
                    }

                    Sm8xInst inst = new(op);
                    bool writes = uniform
                        ? inst.URd == reg && inst.Opcode is 0xabb or 0xab9 or 0x899 or 0x887 or 0x890 or 0x8a4 or 0x882 or 0xc82
                        : inst.Rd == reg && inst.Opcode is 0xb82 or 0x819 or 0x824 or 0x817 or 0x802 or 0x202 or 0x210 or 0x810 or 0x224 or 0x211 or 0x811;

                    if (writes && !Sm8xTable.IsNever(inst))
                    {
                        if (found && p != at)
                        {
                            return false;
                        }

                        found = true;
                        at = p;
                        writer = inst;
                    }
                    else if (visited.Add(p))
                    {
                        if (visited.Count > 256)
                        {
                            return false;
                        }

                        pending.Push(p);
                    }
                }
            }

            return found;
        }

        private static void RecordSm8xUses(ShaderDefinitions definitions, Context context, Sm8xInst inst, Sm8xKind kind)
        {
            switch (kind)
            {
                case Sm8xKind.Attribute:
                    if (inst.Opcode is 0x326 or 0x526)
                    {
                        // IPA: attribute byte address / 4 in bits 64..71 (NAK OpIpa); 0x526 adds a uniform register to it (indexed)
                        SetUserAttributeUses(definitions, context, inst.Bits(64, 8) << 2, 1, isStore: false, indexed: inst.Opcode == 0x526, perPatch: false);
                    }
                    else
                    {
                        // ALD / AST: address 40..49, components - 1 in 74..75, .P 76, .PHYS 77, ALD .O 79 (NAK OpALd / OpASt)
                        bool isStore = inst.Opcode == 0x322 || inst.Bit(79);
                        SetUserAttributeUses(definitions, context, inst.Bits(40, 10), inst.Bits(74, 2) + 1, isStore, inst.Bit(77), inst.Bit(76));
                    }

                    break;
                case Sm8xKind.Local:
                    context.SetUsedFeature(FeatureFlags.LocalMemory);
                    break;
                case Sm8xKind.Shared:
                    context.SetUsedFeature(FeatureFlags.SharedMemory);
                    break;
                case Sm8xKind.Store:
                    context.SetUsedFeature(FeatureFlags.Store);
                    break;
                case Sm8xKind.Shuffle:
                    context.SetUsedFeature(FeatureFlags.Shuffle);
                    break;
            }
        }
    }
}
