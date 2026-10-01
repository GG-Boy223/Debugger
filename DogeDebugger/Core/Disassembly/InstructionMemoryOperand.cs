using DogeDebugger.Core.Modules;
using Iced.Intel;

namespace DogeDebugger.Core.Disassembly;

public sealed record InstructionMemoryOperand(
    int OperandIndex,
    string DisplayText,
    int DefaultValueSize,
    Register MonoBaseRegister = Register.None)
{
    public bool CanParseMonoInstance => MonoBaseRegister != Register.None;
}

public static class InstructionMemoryOperandBuilder
{
    public static IReadOnlyList<InstructionMemoryOperand> Build(
        InstructionSnapshot instruction,
        int bitness,
        AssemblySyntax syntax,
        IReadOnlyList<ModuleDescriptor> modules)
    {
        ArgumentNullException.ThrowIfNull(instruction);
        ArgumentNullException.ThrowIfNull(modules);

        if (!TryDecode(instruction, bitness, out Instruction decoded))
        {
            return [];
        }

        Formatter formatter = CreateFormatter(syntax);
        List<InstructionMemoryOperand> operands = [];
        for (int operandIndex = 0;
             operandIndex < decoded.OpCount;
             operandIndex++)
        {
            if (decoded.GetOpKind(operandIndex) != OpKind.Memory ||
                decoded.MemorySegment is Register.FS or Register.GS)
            {
                continue;
            }

            StringOutput output = new();
            formatter.FormatOperand(decoded, output, operandIndex);
            string displayText = FormatMemoryText(
                output.ToString(),
                decoded,
                modules);
            operands.Add(new InstructionMemoryOperand(
                operandIndex,
                displayText,
                GetValueSize(decoded.MemorySize),
                GetMonoBaseRegister(decoded.MemoryBase, decoded.MemoryIndex, decoded.MemoryDisplacement64, displayText)));
        }

        return operands;
    }

    public static bool TryResolveAddress(
        InstructionSnapshot instruction,
        int bitness,
        int operandIndex,
        IReadOnlyDictionary<string, ulong> registers,
        out ulong address)
    {
        address = 0;
        if (!TryDecode(instruction, bitness, out Instruction decoded) ||
            operandIndex < 0 ||
            operandIndex >= decoded.OpCount ||
            decoded.GetOpKind(operandIndex) != OpKind.Memory)
        {
            return false;
        }

        if (decoded.IsIPRelativeMemoryOperand)
        {
            address = decoded.IPRelativeMemoryAddress;
            return true;
        }

        ulong value = decoded.MemoryDisplacement64;
        if (decoded.MemoryBase != Register.None)
        {
            if (!TryGetRegisterValue(
                    decoded.MemoryBase,
                    registers,
                    out ulong baseValue))
            {
                return false;
            }

            value = unchecked(value + baseValue);
        }

        if (decoded.MemoryIndex != Register.None)
        {
            if (!TryGetRegisterValue(
                    decoded.MemoryIndex,
                    registers,
                    out ulong indexValue))
            {
                return false;
            }

            value = unchecked(
                value + indexValue * (uint)decoded.MemoryIndexScale);
        }

        address = value;
        return true;
    }

    private static bool TryDecode(
        InstructionSnapshot instruction,
        int bitness,
        out Instruction decoded)
    {
        decoded = default;
        if (instruction.Bytes.Length == 0)
        {
            return false;
        }

        Decoder decoder = Decoder.Create(
            bitness == 64 ? 64 : 32,
            new ByteArrayCodeReader(instruction.Bytes));
        decoder.IP = instruction.Address;
        decoded = decoder.Decode();
        return !decoded.IsInvalid && decoded.Length > 0;
    }

    private static string FormatMemoryText(
        string text,
        in Instruction instruction,
        IReadOnlyList<ModuleDescriptor> modules)
    {
        ulong address = instruction.IsIPRelativeMemoryOperand
            ? instruction.IPRelativeMemoryAddress
            : instruction.MemoryBase == Register.None &&
              instruction.MemoryIndex == Register.None
                ? instruction.MemoryDisplacement64
                : 0;
        if (address == 0)
        {
            return text;
        }

        foreach (ModuleDescriptor module in modules)
        {
            ulong end = module.BaseAddress + module.Size;
            if (address >= module.BaseAddress && address < end)
            {
                string absolute = $"0x{address:X}";
                string hexadecimal = address.ToString("X");
                string relative =
                    $"{module.Name}+{unchecked(address - module.BaseAddress):X}";
                string replaced = text.Replace(
                    absolute,
                    relative,
                    StringComparison.OrdinalIgnoreCase);
                replaced = replaced.Replace(
                    $"{hexadecimal}h",
                    relative,
                    StringComparison.OrdinalIgnoreCase);
                return replaced.Replace(
                    hexadecimal,
                    relative,
                    StringComparison.OrdinalIgnoreCase);
            }
        }

        return text;
    }

    public static bool TryGetRegisterValue(
        Register register,
        IReadOnlyDictionary<string, ulong> registers,
        out ulong value)
    {
        value = 0;
        if (register is Register.EIP or Register.RIP)
        {
            return registers.TryGetValue("RIP", out value) ||
                   registers.TryGetValue("EIP", out value);
        }

        string name = register.ToString().ToUpperInvariant();
        if (registers.TryGetValue(name, out value))
        {
            return true;
        }

        if (TryNormalizeRegisterName(name, out string normalized) &&
            registers.TryGetValue(normalized, out value))
        {
            return true;
        }

        return false;
    }

    private static Register GetMonoBaseRegister(
        Register baseRegister,
        Register indexRegister,
        ulong displacement,
        string displayText)
    {
        if (baseRegister is Register.None or Register.EIP or Register.RIP ||
            indexRegister != Register.None ||
            displacement == 0 ||
            !displayText.Contains('[', StringComparison.Ordinal) ||
            !displayText.Contains(']', StringComparison.Ordinal) ||
            !IsGeneralPurposeRegister(baseRegister))
        {
            return Register.None;
        }

        return baseRegister;
    }

    private static bool IsGeneralPurposeRegister(Register register) =>
        register is
            Register.RAX or Register.RCX or Register.RDX or Register.RBX or
            Register.RSP or Register.RBP or Register.RSI or Register.RDI or
            Register.R8 or Register.R9 or Register.R10 or Register.R11 or
            Register.R12 or Register.R13 or Register.R14 or Register.R15 or
            Register.EAX or Register.ECX or Register.EDX or Register.EBX or
            Register.ESP or Register.EBP or Register.ESI or Register.EDI;

    private static bool TryNormalizeRegisterName(
        string name,
        out string normalized)
    {
        normalized = name switch
        {
            "EAX" => "RAX",
            "ECX" => "RCX",
            "EDX" => "RDX",
            "EBX" => "RBX",
            "ESP" => "RSP",
            "EBP" => "RBP",
            "ESI" => "RSI",
            "EDI" => "RDI",
            "AX" => "RAX",
            "CX" => "RCX",
            "DX" => "RDX",
            "BX" => "RBX",
            "SP" => "RSP",
            "BP" => "RBP",
            "SI" => "RSI",
            "DI" => "RDI",
            "AL" => "RAX",
            "CL" => "RCX",
            "DL" => "RDX",
            "BL" => "RBX",
            "AH" => "RAX",
            "CH" => "RCX",
            "DH" => "RDX",
            "BH" => "RBX",
            _ => string.Empty
        };
        if (normalized.Length == 0 &&
            name.Length >= 2 &&
            name[0] == 'R' &&
            char.IsDigit(name[1]))
        {
            int suffixIndex = 2;
            while (suffixIndex < name.Length &&
                   char.IsDigit(name[suffixIndex]))
            {
                suffixIndex++;
            }

            if (suffixIndex < name.Length &&
                name[suffixIndex] is 'D' or 'W' or 'B')
            {
                normalized = name[..suffixIndex];
            }
        }

        return normalized.Length > 0;
    }

    private static int GetValueSize(MemorySize memorySize) =>
        memorySize switch
        {
            MemorySize.Int8 or MemorySize.UInt8 => 1,
            MemorySize.Int16 or MemorySize.UInt16 => 2,
            MemorySize.Int32 or MemorySize.UInt32 or
                MemorySize.Float32 => 4,
            MemorySize.Int64 or MemorySize.UInt64 or
                MemorySize.Float64 => 8,
            _ => 8
        };

    private static Formatter CreateFormatter(AssemblySyntax syntax)
    {
        Formatter formatter = syntax switch
        {
            AssemblySyntax.Masm => new MasmFormatter(),
            AssemblySyntax.Nasm => new NasmFormatter(),
            AssemblySyntax.Gas => new GasFormatter(),
            _ => new IntelFormatter()
        };
        if (syntax is AssemblySyntax.Intel or AssemblySyntax.Masm)
        {
            formatter.Options.HexPrefix = string.Empty;
            formatter.Options.HexSuffix = "h";
        }
        else
        {
            formatter.Options.HexPrefix = "0x";
            formatter.Options.HexSuffix = string.Empty;
        }

        formatter.Options.UppercaseHex = true;
        return formatter;
    }
}
