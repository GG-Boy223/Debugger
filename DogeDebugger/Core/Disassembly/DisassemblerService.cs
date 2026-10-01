using Iced.Intel;
using StringBuilder = System.Text.StringBuilder;

namespace DogeDebugger.Core.Disassembly;

public sealed class DisassemblerService
{
    public IReadOnlyList<InstructionSnapshot> Disassemble(
        ReadOnlySpan<byte> bytes,
        ulong address,
        int instructionCount,
        bool is64Bit,
        DisassemblyOptions? options = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(instructionCount);
        options ??= new DisassemblyOptions();

        if (bytes.IsEmpty || instructionCount == 0)
        {
            return [];
        }

        ByteArrayCodeReader reader = new(bytes.ToArray());
        Decoder decoder = Decoder.Create(is64Bit ? 64 : 32, reader);
        decoder.IP = address;
        Formatter formatter = options.Syntax switch
        {
            AssemblySyntax.Masm => new MasmFormatter(),
            AssemblySyntax.Nasm => new NasmFormatter(),
            AssemblySyntax.Gas => new GasFormatter(),
            _ => new IntelFormatter()
        };
        formatter.Options.SignedImmediateOperands =
            options.UseSignedImmediateOperands;

        if (options.UppercaseHex)
        {
            formatter.Options.UppercaseHex = true;
        }
        else
        {
            formatter.Options.UppercaseHex = false;
        }

        if (options.Syntax is AssemblySyntax.Intel or AssemblySyntax.Masm)
        {
            formatter.Options.HexPrefix = string.Empty;
            formatter.Options.HexSuffix = "h";
        }
        else
        {
            formatter.Options.HexPrefix = "0x";
            formatter.Options.HexSuffix = string.Empty;
        }

        List<InstructionSnapshot> instructions = new(instructionCount);
        while (instructions.Count < instructionCount &&
               decoder.IP - address < (ulong)bytes.Length)
        {
            Instruction instruction = decoder.Decode();
            if (instruction.IsInvalid)
            {
                break;
            }

            StringOutput output = new();
            formatter.Format(instruction, output);
            byte[] encoded = bytes
                .Slice(
                    checked((int)(instruction.IP - address)),
                    Math.Min(instruction.Length, bytes.Length - checked((int)(instruction.IP - address))))
                .ToArray();
            ConstantOffsets constantOffsets = decoder.GetConstantOffsets(instruction);
            byte[] fixedMask = Enumerable.Repeat((byte)1, encoded.Length).ToArray();
            MarkWildcard(fixedMask, constantOffsets.DisplacementOffset, constantOffsets.DisplacementSize);
            MarkWildcard(fixedMask, constantOffsets.ImmediateOffset, constantOffsets.ImmediateSize);
            MarkWildcard(fixedMask, constantOffsets.ImmediateOffset2, constantOffsets.ImmediateSize2);

            List<ulong> references = [];
            if (instruction.NearBranchTarget != 0 &&
                instruction.FlowControl is FlowControl.Call or
                    FlowControl.UnconditionalBranch or
                    FlowControl.ConditionalBranch)
            {
                references.Add(instruction.NearBranchTarget);
            }

            if (instruction.IsIPRelativeMemoryOperand)
            {
                references.Add(instruction.IPRelativeMemoryAddress);
            }

            List<ulong> immediates = [];
            List<ulong> absoluteMemoryAddresses = [];
            for (int operandIndex = 0;
                 operandIndex < instruction.OpCount;
                 operandIndex++)
            {
                OpKind operandKind = instruction.GetOpKind(operandIndex);
                if (operandKind == OpKind.Memory)
                {
                    absoluteMemoryAddresses.Add(instruction.MemoryDisplacement64);
                }

                ulong? immediate = operandKind switch
                {
                    OpKind.Immediate8 => instruction.Immediate8,
                    OpKind.Immediate8_2nd => instruction.Immediate8_2nd,
                    OpKind.Immediate16 => instruction.Immediate16,
                    OpKind.Immediate32 => instruction.Immediate32,
                    OpKind.Immediate64 => instruction.Immediate64,
                    OpKind.Immediate8to16 => (ulong)(long)instruction.Immediate8to16,
                    OpKind.Immediate8to32 => (ulong)(long)instruction.Immediate8to32,
                    OpKind.Immediate8to64 => (ulong)instruction.Immediate8to64,
                    OpKind.Immediate32to64 => (ulong)instruction.Immediate32to64,
                    _ => null
                };
                if (immediate is not null)
                {
                    immediates.Add(immediate.Value);
                }
            }

            instructions.Add(new InstructionSnapshot
            {
                Address = instruction.IP,
                Length = instruction.Length,
                Bytes = encoded,
                Text = output.ToString(),
                Mnemonic = instruction.Mnemonic.ToString().ToLowerInvariant(),
                Operands = FormatOperands(instruction, formatter, options),
                FlowControl = instruction.FlowControl.ToString(),
                NearBranchTarget = instruction.NearBranchTarget,
                ReferencedAddresses = references,
                ImmediateValues = immediates,
                AbsoluteMemoryAddresses = absoluteMemoryAddresses,
                FixedByteMask = fixedMask,
                DisplayAddressText = FormatAddress(
                    instruction.IP,
                    options),
                DisplayBytesText = FormatBytes(
                    encoded,
                    options.BytesStyle),
                ArrowText = FormatArrow(instruction, options)
            });
        }

        return instructions;
    }

    public InstructionSnapshot? DisassembleOne(
        ReadOnlySpan<byte> bytes,
        ulong address,
        bool is64Bit,
        DisassemblyOptions? options = null)
    {
        IReadOnlyList<InstructionSnapshot> result =
            Disassemble(bytes, address, 1, is64Bit, options);
        return result.Count == 0 ? null : result[0];
    }

    private static string FormatOperands(
        Instruction instruction,
        Formatter formatter,
        DisassemblyOptions options)
    {
        if (instruction.NearBranchTarget != 0 &&
            instruction.FlowControl is
                FlowControl.Call or
                FlowControl.UnconditionalBranch or
                FlowControl.ConditionalBranch)
        {
            return FormatBranchTarget(instruction.NearBranchTarget, options);
        }

        StringOutput output = new();
        formatter.FormatAllOperands(instruction, output);
        return output.ToString();
    }

    private static string FormatBranchTarget(
        ulong target,
        DisassemblyOptions options)
    {
        foreach (DisassemblyModuleRange module in options.Modules)
        {
            if (target >= module.BaseAddress &&
                target < module.BaseAddress + module.Size)
            {
                return $"{module.Name}+{unchecked(target - module.BaseAddress):X}";
            }
        }

        return $"0x{target:X}";
    }

    private static string FormatAddress(
        ulong address,
        DisassemblyOptions options)
    {
        return options.AddressMode switch
        {
            AssemblyAddressMode.Rva =>
                $"0x{unchecked(address - options.RelativeBase):X}",
            AssemblyAddressMode.ModuleOffset
                when !string.IsNullOrWhiteSpace(options.ModuleName) =>
                $"{options.ModuleName}+{unchecked(address - options.RelativeBase):X}",
            _ => $"{address:X16}"
        };
    }

    private static string FormatBytes(
        byte[] bytes,
        DisassemblyBytesStyle style)
    {
        if (bytes.Length == 0)
        {
            return string.Empty;
        }

        if (style == DisassemblyBytesStyle.CheatEngine)
        {
            return string.Join(
                ' ',
                bytes.Select(static value => value.ToString("X2")));
        }

        string first = bytes[0].ToString("X2");
        if (bytes.Length == 1)
        {
            return first;
        }

        bool hasRexPrefix = bytes[0] is >= 0x40 and <= 0x4F;
        string rest = hasRexPrefix
            ? FormatBytePairs(bytes.AsSpan(1))
            : Convert.ToHexString(bytes.AsSpan(1));
        return hasRexPrefix
            ? $"{first}:{rest}"
            : $"{first} {rest}";
    }

    private static string FormatBytePairs(ReadOnlySpan<byte> bytes)
    {
        StringBuilder builder = new(bytes.Length * 2 + 8);
        for (int offset = 0; offset < bytes.Length; offset += 2)
        {
            if (offset > 0)
            {
                builder.Append(' ');
            }

            builder.Append(bytes[offset].ToString("X2"));
            if (offset + 1 < bytes.Length)
            {
                builder.Append(bytes[offset + 1].ToString("X2"));
            }
        }

        return builder.ToString();
    }

    private static string FormatArrow(
        Instruction instruction,
        DisassemblyOptions options)
    {
        if (!options.ShowJumpArrows ||
            instruction.NearBranchTarget == 0 ||
            instruction.FlowControl is not (
                FlowControl.Call or
                FlowControl.UnconditionalBranch or
                FlowControl.ConditionalBranch))
        {
            return string.Empty;
        }

        return instruction.NearBranchTarget < instruction.IP ? "↰" : "↳";
    }

    private static void MarkWildcard(byte[] mask, int offset, int size)
    {
        if (offset < 0 || size <= 0 || offset >= mask.Length)
        {
            return;
        }

        int end = Math.Min(mask.Length, offset + size);
        for (int index = offset; index < end; index++)
        {
            mask[index] = 0;
        }
    }
}
