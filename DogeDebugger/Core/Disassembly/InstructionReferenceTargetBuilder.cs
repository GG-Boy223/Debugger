using DogeDebugger.Core.Modules;
using Iced.Intel;

namespace DogeDebugger.Core.Disassembly;

public sealed record InstructionReferenceTarget(
    InstructionReferenceKind Kind,
    ulong Value)
{
    public string DisplayText => Kind == InstructionReferenceKind.Address
        ? $"地址 0x{Value:X16}"
        : $"常量 0x{Value:X}";
}

public static class InstructionReferenceTargetBuilder
{
    public static IReadOnlyList<InstructionReferenceTarget> Build(
        InstructionSnapshot instruction,
        int bitness,
        IReadOnlyList<ModuleDescriptor> modules)
    {
        ArgumentNullException.ThrowIfNull(instruction);
        ArgumentNullException.ThrowIfNull(modules);

        List<InstructionReferenceTarget> targets = [];
        if (instruction.Bytes.Length > 0)
        {
            Decoder decoder = Decoder.Create(
                bitness == 64 ? 64 : 32,
                new ByteArrayCodeReader(instruction.Bytes));
            decoder.IP = instruction.Address;
            Instruction decoded = decoder.Decode();
            if (!decoded.IsInvalid && decoded.Length > 0)
            {
                ConstantOffsets constantOffsets =
                    decoder.GetConstantOffsets(decoded);
                for (int operandIndex = 0;
                     operandIndex < decoded.OpCount;
                     operandIndex++)
                {
                    OpKind operandKind = decoded.GetOpKind(operandIndex);
                    if (IsNearBranch(operandKind))
                    {
                        AddTarget(
                            targets,
                            InstructionReferenceKind.Address,
                            decoded.NearBranchTarget);
                    }
                    else if (operandKind == OpKind.Memory)
                    {
                        AddMemoryTarget(
                            targets,
                            in decoded,
                            in constantOffsets);
                    }
                    else if (IsImmediate(operandKind))
                    {
                        ulong value = decoded.GetImmediate(operandIndex);
                        AddTarget(
                            targets,
                            IsAddressInModules(value, modules)
                                ? InstructionReferenceKind.Address
                                : InstructionReferenceKind.Constant,
                            value);
                    }
                }
            }
        }

        if (targets.Count == 0)
        {
            AddTarget(
                targets,
                InstructionReferenceKind.Address,
                instruction.Address);
        }

        return targets;
    }

    private static void AddMemoryTarget(
        List<InstructionReferenceTarget> targets,
        in Instruction instruction,
        in ConstantOffsets constantOffsets)
    {
        if (!constantOffsets.HasDisplacement)
        {
            return;
        }

        if (instruction.MemorySegment is Register.FS or Register.GS)
        {
            AddTarget(
                targets,
                InstructionReferenceKind.Constant,
                instruction.MemoryDisplacement64);
            return;
        }

        if (instruction.MemoryBase is Register.EIP or Register.RIP)
        {
            AddTarget(
                targets,
                InstructionReferenceKind.Address,
                instruction.IPRelativeMemoryAddress);
            return;
        }

        if (instruction.MemoryBase == Register.None &&
            instruction.MemoryIndex == Register.None)
        {
            AddTarget(
                targets,
                InstructionReferenceKind.Address,
                instruction.MemoryDisplacement64);
            return;
        }

        AddTarget(
            targets,
            InstructionReferenceKind.Constant,
            instruction.MemoryDisplacement64);
    }

    private static bool IsAddressInModules(
        ulong value,
        IReadOnlyList<ModuleDescriptor> modules)
    {
        foreach (ModuleDescriptor module in modules)
        {
            ulong end = module.BaseAddress + module.Size;
            if (end < module.BaseAddress)
            {
                if (value >= module.BaseAddress)
                {
                    return true;
                }
            }
            else if (value >= module.BaseAddress && value < end)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsNearBranch(OpKind kind) =>
        kind is OpKind.NearBranch16 or
            OpKind.NearBranch32 or
            OpKind.NearBranch64;

    private static bool IsImmediate(OpKind kind) =>
        kind is >= OpKind.Immediate8 and <= OpKind.Immediate32to64;

    private static void AddTarget(
        List<InstructionReferenceTarget> targets,
        InstructionReferenceKind kind,
        ulong value)
    {
        InstructionReferenceTarget target = new(kind, value);
        if (!targets.Contains(target))
        {
            targets.Add(target);
        }
    }
}
