using System.Globalization;
using Iced.Intel;

namespace DogeDebugger.Core.Scripting.AutoAssembler;

/// <summary>
/// Encodes one Auto Assembler instruction line into machine code. Symbols and
/// labels are substituted to hexadecimal literals before encoder dispatch, so
/// this class only deals with registers, numbers and memory operands. The
/// accepted instruction set mirrors the constructs produced by the original
/// templates and the common hook scripts built on top of them.
/// </summary>
internal static class AutoAssemblerTextAssembler
{
    public static byte[] Assemble(string text, ulong address, bool is64Bit)
    {
        string trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return [];
        }

        int mnemonicEnd = 0;
        while (mnemonicEnd < trimmed.Length &&
               char.IsLetterOrDigit(trimmed[mnemonicEnd]))
        {
            mnemonicEnd++;
        }

        if (mnemonicEnd == 0)
        {
            throw new AutoAssemblerException($"无法识别的指令 '{text}'");
        }

        string mnemonic = trimmed[..mnemonicEnd].ToLowerInvariant();
        string operandText = trimmed[mnemonicEnd..].Trim();
        string[] operands = SplitOperands(operandText);

        Instruction instruction = mnemonic switch
        {
            "nop" => Instruction.Create(Code.Nopd),
            "ret" or "retn" => CreateRet(operands),
            "int3" => Instruction.Create(Code.Int3),
            "int" => Instruction.Create(Code.Int_imm8, checked((byte)ParseNumber(operands[0]))),
            "leave" => Instruction.Create(Code.Leaveq),
            "cld" => Instruction.Create(Code.Cld),
            "std" => Instruction.Create(Code.Std),
            "cli" => Instruction.Create(Code.Cli),
            "sti" => Instruction.Create(Code.Sti),
            "hlt" => Instruction.Create(Code.Hlt),
            "pause" => Instruction.Create(Code.Pause),
            "syscall" => Instruction.Create(Code.Syscall),
            "ud2" => Instruction.Create(Code.Ud2),
            "cpuid" => Instruction.Create(Code.Cpuid),
            "rdtsc" => Instruction.Create(Code.Rdtsc),
            "lfence" => Instruction.Create(Code.Lfence),
            "mfence" => Instruction.Create(Code.Mfence),
            "sfence" => Instruction.Create(Code.Sfence),
            "push" => CreatePush(operands, is64Bit),
            "pop" => instruction_Pop(operands, is64Bit),
            "mov" => CreateMov(operands, is64Bit),
            "movzx" => CreateMovExtend(operands, zeroExtend: true),
            "movsx" => CreateMovExtend(operands, zeroExtend: false),
            "lea" => CreateLea(operands, is64Bit),
            "add" => CreateBinary(operands, BinaryKind.Add, is64Bit),
            "sub" => CreateBinary(operands, BinaryKind.Sub, is64Bit),
            "and" => CreateBinary(operands, BinaryKind.And, is64Bit),
            "or" => CreateBinary(operands, BinaryKind.Or, is64Bit),
            "xor" => CreateBinary(operands, BinaryKind.Xor, is64Bit),
            "cmp" => CreateBinary(operands, BinaryKind.Cmp, is64Bit),
            "test" => CreateBinary(operands, BinaryKind.Test, is64Bit),
            "adc" => CreateBinary(operands, BinaryKind.Adc, is64Bit),
            "sbb" => CreateBinary(operands, BinaryKind.Sbb, is64Bit),
            "inc" => CreateUnary(operands, UnaryKind.Inc, is64Bit),
            "dec" => CreateUnary(operands, UnaryKind.Dec, is64Bit),
            "not" => CreateUnary(operands, UnaryKind.Not, is64Bit),
            "neg" => CreateUnary(operands, UnaryKind.Neg, is64Bit),
            "mul" => CreateUnary(operands, UnaryKind.Mul, is64Bit),
            "div" => CreateUnary(operands, UnaryKind.Div, is64Bit),
            "idiv" => CreateUnary(operands, UnaryKind.IDiv, is64Bit),
            "xchg" => CreateXchg(operands, is64Bit),
            "imul" => CreateImul(operands, is64Bit),
            "shl" or "sal" => CreateShift(operands, ShiftKind.Shl, is64Bit),
            "shr" => CreateShift(operands, ShiftKind.Shr, is64Bit),
            "sar" => CreateShift(operands, ShiftKind.Sar, is64Bit),
            "rol" => CreateShift(operands, ShiftKind.Rol, is64Bit),
            "ror" => CreateShift(operands, ShiftKind.Ror, is64Bit),
            "rcl" => CreateShift(operands, ShiftKind.Rcl, is64Bit),
            "rcr" => CreateShift(operands, ShiftKind.Rcr, is64Bit),
            "jmp" => CreateJump(operands, is64Bit),
            "call" => CreateCall(operands, is64Bit),
            "retf" => throw new AutoAssemblerException($"暂不支持的指令 '{text}'"),
            _ when TryCreateJumpCondition(mnemonic, operands, is64Bit, out Instruction branch) => branch,
            _ => throw new AutoAssemblerException($"无法识别的指令 '{text}'")
        };

        WrittenBytes writer = new();
        Encoder encoder = Encoder.Create(is64Bit ? 64 : 32, writer);
        encoder.Encode(ref instruction, address);
        return writer.ToArray();
    }

    private static Instruction instruction_Pop(string[] operands, bool is64Bit)
    {
        if (operands.Length != 1)
        {
            throw new AutoAssemblerException("pop 需要 1 个操作数");
        }

        if (TryParseRegister(operands[0], out Register register))
        {
            return Instruction.Create(is64Bit ? Code.Pop_r64 : Code.Pop_r32, register);
        }

        MemoryOperand memory = ParseMemory(operands[0]);
        return Instruction.Create(
            is64Bit ? Code.Pop_rm64 : Code.Pop_rm32,
            memory);
    }

    private static Instruction CreateRet(string[] operands) =>
        operands.Length == 0
            ? Instruction.Create(Code.Retnq)
            : Instruction.Create(Code.Retnq_imm16, checked((ushort)ParseNumber(operands[0])));

    private static Instruction CreatePush(string[] operands, bool is64Bit)
    {
        if (operands.Length != 1)
        {
            throw new AutoAssemblerException("push 需要 1 个操作数");
        }

        if (TryParseRegister(operands[0], out Register register))
        {
            return Instruction.Create(is64Bit ? Code.Push_r64 : Code.Push_r32, register);
        }

        if (operands[0].StartsWith('['))
        {
            MemoryOperand memory = ParseMemory(operands[0]);
            return Instruction.Create(is64Bit ? Code.Push_rm64 : Code.Push_rm32, memory);
        }

        long value = (long)ParseNumber(operands[0]);
        bool byteImmediate = value is >= sbyte.MinValue and <= sbyte.MaxValue;
        if (is64Bit)
        {
            return byteImmediate
                ? Instruction.Create(Code.Pushq_imm8, unchecked((int)value))
                : Instruction.Create(Code.Pushq_imm32, unchecked((int)value));
        }

        return byteImmediate
            ? Instruction.Create(Code.Pushd_imm8, unchecked((int)value))
            : Instruction.Create(Code.Pushd_imm32, unchecked((int)value));
    }

    private static Instruction CreateMov(string[] operands, bool is64Bit)
    {
        if (operands.Length != 2)
        {
            throw new AutoAssemblerException("mov 需要 2 个操作数");
        }

        string destination = operands[0];
        string source = operands[1];
        if (TryParseRegister(destination, out Register destRegister))
        {
            if (TryParseRegister(source, out Register sourceRegister))
            {
                int size = Math.Max(RegisterSize(destRegister), RegisterSize(sourceRegister));
                (Register target, Register normalizedSource) = NormalizePair(destRegister, sourceRegister, size);
                return Instruction.Create(
                    size switch
                    {
                        1 => Code.Mov_r8_rm8,
                        2 => Code.Mov_r16_rm16,
                        8 => Code.Mov_r64_rm64,
                        _ => Code.Mov_r32_rm32
                    },
                    target,
                    normalizedSource);
            }

            if (source.StartsWith('['))
            {
                MemoryOperand memory = ParseMemory(source);
                int size = OperandSizeHint(source, RegisterSize(destRegister));
                return Instruction.Create(
                    size switch
                    {
                        1 => Code.Mov_r8_rm8,
                        2 => Code.Mov_r16_rm16,
                        8 => Code.Mov_r64_rm64,
                        _ => Code.Mov_r32_rm32
                    },
                    destRegister,
                    memory);
            }

            ulong value = ParseNumber(source);
            int registerSize = RegisterSize(destRegister);
            return registerSize switch
            {
                1 => Instruction.Create(Code.Mov_r8_imm8, destRegister, checked((byte)value)),
                2 => Instruction.Create(Code.Mov_r16_imm16, destRegister, checked((ushort)value)),
                8 => Instruction.Create(Code.Mov_r64_imm64, destRegister, value),
                _ => Instruction.Create(Code.Mov_r32_imm32, destRegister, checked((uint)value))
            };
        }

        MemoryOperand destinationMemory = ParseMemory(destination);
        int destinationSize = OperandSizeHint(destination, is64Bit ? 8 : 4);
        if (TryParseRegister(source, out Register valueRegister))
        {
            int size = OperandSizeHint(destination, RegisterSize(valueRegister));
            (Register normalizedSource, Register target) = NormalizePair(valueRegister, valueRegister, size);
            Register effectiveSource = size == RegisterSize(valueRegister)
                ? valueRegister
                : normalizedSource;
            return Instruction.Create(
                size switch
                {
                    1 => Code.Mov_rm8_r8,
                    2 => Code.Mov_rm16_r16,
                    8 => Code.Mov_rm64_r64,
                    _ => Code.Mov_rm32_r32
                },
                destinationMemory,
                effectiveSource);
        }

        ulong immediate = ParseNumber(source);
        return destinationSize switch
        {
            1 => Instruction.Create(Code.Mov_rm8_imm8, destinationMemory, checked((byte)immediate)),
            2 => Instruction.Create(Code.Mov_rm16_imm16, destinationMemory, checked((ushort)immediate)),
            8 => Instruction.Create(Code.Mov_rm64_imm32, destinationMemory, unchecked((int)immediate)),
            _ => Instruction.Create(Code.Mov_rm32_imm32, destinationMemory, unchecked((uint)immediate))
        };
    }

    private static Instruction CreateMovExtend(string[] operands, bool zeroExtend)
    {
        if (operands.Length != 2)
        {
            throw new AutoAssemblerException("movzx/movsx 需要 2 个操作数");
        }

        if (!TryParseRegister(operands[0], out Register destination))
        {
            throw new AutoAssemblerException("movzx/movsx 的目标必须是寄存器");
        }

        int destinationSize = RegisterSize(destination);
        if (TryParseRegister(operands[1], out Register source))
        {
            int sourceSize = RegisterSize(source);
            return CreateMovExtendInstruction(
                destination,
                destinationSize,
                sourceSize,
                memory: null,
                sourceRegister: source,
                zeroExtend);
        }

        MemoryOperand memory = ParseMemory(operands[1]);
        int hinted = OperandSizeHint(operands[1], 1);
        return CreateMovExtendInstruction(
            destination,
            destinationSize,
            hinted,
            memory,
            Register.None,
            zeroExtend);
    }

    private static Instruction CreateMovExtendInstruction(
        Register destination,
        int destinationSize,
        int sourceSize,
        MemoryOperand? memory,
        Register sourceRegister,
        bool zeroExtend)
    {
        if (sourceSize is not (1 or 2))
        {
            throw new AutoAssemblerException("movzx/movsx 仅支持 8/16 位源操作数");
        }

        Code code = (destinationSize, sourceSize, zeroExtend) switch
        {
            (8, 1, true) => Code.Movzx_r64_rm8,
            (8, 2, true) => Code.Movzx_r64_rm16,
            (4, 1, true) => Code.Movzx_r32_rm8,
            (4, 2, true) => Code.Movzx_r32_rm16,
            (8, 1, false) => Code.Movsx_r64_rm8,
            (8, 2, false) => Code.Movsx_r64_rm16,
            (4, 1, false) => Code.Movsx_r32_rm8,
            (4, 2, false) => Code.Movsx_r32_rm16,
            _ => throw new AutoAssemblerException("movzx/movsx 的操作数大小组合不受支持")
        };

        return memory is { } memoryOperand
            ? Instruction.Create(code, destination, memoryOperand)
            : Instruction.Create(code, destination, sourceRegister);
    }

    private static Instruction CreateLea(string[] operands, bool is64Bit)
    {
        if (operands.Length != 2 || !TryParseRegister(operands[0], out Register destination))
        {
            throw new AutoAssemblerException("lea 需要寄存器与内存操作数");
        }

        int size = OperandSizeHint(operands[0], is64Bit ? 8 : 4);
        MemoryOperand memory = ParseMemory(operands[1]);
        return Instruction.Create(
            size switch
            {
                2 => Code.Lea_r16_m,
                8 => Code.Lea_r64_m,
                _ => Code.Lea_r32_m
            },
            destination,
            memory);
    }

    private static Instruction CreateBinary(string[] operands, BinaryKind kind, bool is64Bit)
    {
        if (operands.Length != 2)
        {
            throw new AutoAssemblerException("该指令需要 2 个操作数");
        }

        bool memorySource = operands[1].StartsWith('[');
        bool registerSource = TryParseRegister(operands[1], out Register sourceRegister);
        MemoryOperand sourceMemory = memorySource
            ? ParseMemory(operands[1])
            : default;

        if (TryParseRegister(operands[0], out Register destinationRegister))
        {
            int size = RegisterSize(destinationRegister);
            if (registerSource)
            {
                int effectiveSize = Math.Max(size, RegisterSize(sourceRegister));
                (Register target, Register normalizedSource) =
                    NormalizePair(destinationRegister, sourceRegister, effectiveSize);
                return Instruction.Create(
                    BinaryRegisterRegisterCode(kind, effectiveSize),
                    target,
                    normalizedSource);
            }

            if (memorySource)
            {
                int effectiveSize = OperandSizeHint(operands[1], size);
                return Instruction.Create(
                    BinaryRegisterMemoryCode(kind, effectiveSize),
                    destinationRegister,
                    sourceMemory);
            }

            long immediate = (long)ParseNumber(operands[1]);
            return Instruction.Create(
                SelectImmediateCode(kind, size, immediate, registerDestination: true),
                destinationRegister,
                size == 8
                    ? unchecked((int)immediate)
                    : size == 2
                        ? unchecked((short)immediate)
                        : unchecked((int)immediate));
        }

        MemoryOperand destinationMemory = ParseMemory(operands[0]);
        int destinationSize = OperandSizeHint(operands[0], is64Bit ? 8 : 4);
        if (registerSource)
        {
            int size = OperandSizeHint(operands[0], RegisterSize(sourceRegister));
            return Instruction.Create(
                BinaryMemoryRegisterCode(kind, size),
                destinationMemory,
                sourceRegister);
        }

        long immediateValue = (long)ParseNumber(operands[1]);
        return Instruction.Create(
            SelectImmediateCode(kind, destinationSize, immediateValue, registerDestination: false),
            destinationMemory,
            destinationSize == 8
                ? unchecked((int)immediateValue)
                : destinationSize == 2
                    ? unchecked((short)immediateValue)
                    : unchecked((int)immediateValue));
    }

    private static Instruction CreateUnary(string[] operands, UnaryKind kind, bool is64Bit)
    {
        if (operands.Length != 1)
        {
            throw new AutoAssemblerException("该指令需要 1 个操作数");
        }

        if (TryParseRegister(operands[0], out Register register))
        {
            int size = RegisterSize(register);
            return Instruction.Create(UnaryMemoryCode(kind, size), register);
        }

        MemoryOperand memory = ParseMemory(operands[0]);
        int memorySize = OperandSizeHint(operands[0], is64Bit ? 8 : 4);
        return Instruction.Create(UnaryMemoryCode(kind, memorySize), memory);
    }

    private static Instruction CreateXchg(string[] operands, bool is64Bit)
    {
        if (operands.Length != 2 ||
            !TryParseRegister(operands[0], out Register first) ||
            !TryParseRegister(operands[1], out Register second))
        {
            throw new AutoAssemblerException("xchg 需要两个寄存器操作数");
        }

        int size = Math.Max(RegisterSize(first), RegisterSize(second));
        (Register target, Register source) = NormalizePair(first, second, size);
        return Instruction.Create(
            size switch
            {
                1 => Code.Xchg_rm8_r8,
                2 => Code.Xchg_rm16_r16,
                8 => Code.Xchg_rm64_r64,
                _ => Code.Xchg_rm32_r32
            },
            target,
            source);
    }

    private static Instruction CreateImul(string[] operands, bool is64Bit)
    {
        if (operands.Length == 1)
        {
            if (TryParseRegister(operands[0], out Register single))
            {
                return Instruction.Create(
                    RegisterSize(single) == 8 ? Code.Imul_rm64 : Code.Imul_rm32,
                    single);
            }

            MemoryOperand memory = ParseMemory(operands[0]);
            return Instruction.Create(
                OperandSizeHint(operands[0], is64Bit ? 8 : 4) == 8
                    ? Code.Imul_rm64
                    : Code.Imul_rm32,
                memory);
        }

        if (operands.Length == 2 && TryParseRegister(operands[0], out Register destination))
        {
            if (TryParseRegister(operands[1], out Register source))
            {
                int size = Math.Max(RegisterSize(destination), RegisterSize(source));
                (Register target, Register normalizedSource) =
                    NormalizePair(destination, source, size);
                return Instruction.Create(
                    size == 8 ? Code.Imul_r64_rm64 : Code.Imul_r32_rm32,
                    target,
                    normalizedSource);
            }

            MemoryOperand memory = ParseMemory(operands[1]);
            return Instruction.Create(
                RegisterSize(destination) == 8 ? Code.Imul_r64_rm64 : Code.Imul_r32_rm32,
                destination,
                memory);
        }

        if (operands.Length == 3 && TryParseRegister(operands[0], out Register multiDestination))
        {
            long immediate = (long)ParseNumber(operands[2]);
            bool useByteImmediate = immediate is >= sbyte.MinValue and <= sbyte.MaxValue;
            if (TryParseRegister(operands[1], out Register multiSource))
            {
                int size = Math.Max(RegisterSize(multiDestination), RegisterSize(multiSource));
                (Register target, Register normalizedSource) =
                    NormalizePair(multiDestination, multiSource, size);
                return useByteImmediate
                    ? Instruction.Create(
                        size == 8 ? Code.Imul_r64_rm64_imm8 : Code.Imul_r32_rm32_imm8,
                        target,
                        normalizedSource,
                        unchecked((sbyte)immediate))
                    : Instruction.Create(
                        size == 8 ? Code.Imul_r64_rm64_imm32 : Code.Imul_r32_rm32_imm32,
                        target,
                        normalizedSource,
                        unchecked((int)immediate));
            }

            MemoryOperand memory = ParseMemory(operands[1]);
            return useByteImmediate
                ? Instruction.Create(
                    RegisterSize(multiDestination) == 8
                        ? Code.Imul_r64_rm64_imm8
                        : Code.Imul_r32_rm32_imm8,
                    multiDestination,
                    memory,
                    unchecked((sbyte)immediate))
                : Instruction.Create(
                    RegisterSize(multiDestination) == 8
                        ? Code.Imul_r64_rm64_imm32
                        : Code.Imul_r32_rm32_imm32,
                    multiDestination,
                    memory,
                    unchecked((int)immediate));
        }

        throw new AutoAssemblerException("imul 操作数形式不受支持");
    }

    private static Instruction CreateShift(string[] operands, ShiftKind kind, bool is64Bit)
    {
        if (operands.Length != 2)
        {
            throw new AutoAssemblerException("移位指令需要 2 个操作数");
        }

        bool byCl = string.Equals(operands[1].Trim(), "cl", StringComparison.OrdinalIgnoreCase);
        if (TryParseRegister(operands[0], out Register register))
        {
            int size = RegisterSize(register);
            if (byCl)
            {
                return Instruction.Create(ShiftRegisterClCode(kind, size), register);
            }

            byte amount = checked((byte)ParseNumber(operands[1]));
            return Instruction.Create(ShiftRegisterImmediateCode(kind, size), register, amount);
        }

        MemoryOperand memory = ParseMemory(operands[0]);
        int memorySize = OperandSizeHint(operands[0], is64Bit ? 8 : 4);
        if (byCl)
        {
            return Instruction.Create(ShiftMemoryClCode(kind, memorySize), memory);
        }

        return Instruction.Create(
            ShiftMemoryImmediateCode(kind, memorySize),
            memory,
            checked((byte)ParseNumber(operands[1])));
    }

    private static Instruction CreateJump(string[] operands, bool is64Bit)
    {
        if (operands.Length != 1)
        {
            throw new AutoAssemblerException("jmp 需要 1 个操作数");
        }

        if (TryParseRegister(operands[0], out Register register))
        {
            return Instruction.Create(is64Bit ? Code.Jmp_rm64 : Code.Jmp_rm32, register);
        }

        if (operands[0].StartsWith('['))
        {
            return Instruction.Create(
                is64Bit ? Code.Jmp_rm64 : Code.Jmp_rm32,
                ParseMemory(operands[0]));
        }

        ulong target = ParseNumber(operands[0]);
        return Instruction.CreateBranch(is64Bit ? Code.Jmp_rel32_64 : Code.Jmp_rel32_32, target);
    }

    private static Instruction CreateCall(string[] operands, bool is64Bit)
    {
        if (operands.Length != 1)
        {
            throw new AutoAssemblerException("call 需要 1 个操作数");
        }

        if (TryParseRegister(operands[0], out Register register))
        {
            return Instruction.Create(is64Bit ? Code.Call_rm64 : Code.Call_rm32, register);
        }

        if (operands[0].StartsWith('['))
        {
            return Instruction.Create(
                is64Bit ? Code.Call_rm64 : Code.Call_rm32,
                ParseMemory(operands[0]));
        }

        ulong target = ParseNumber(operands[0]);
        return Instruction.CreateBranch(is64Bit ? Code.Call_rel32_64 : Code.Call_rel32_32, target);
    }

    private static bool TryCreateJumpCondition(
        string mnemonic,
        string[] operands,
        bool is64Bit,
        out Instruction instruction)
    {
        instruction = default;
        if (operands.Length != 1 || !TryParseJumpConditionCode(mnemonic, is64Bit, out Code code))
        {
            return false;
        }

        instruction = Instruction.CreateBranch(code, ParseNumber(operands[0]));
        return true;
    }

    private static bool TryParseJumpConditionCode(string mnemonic, bool is64Bit, out Code code)
    {
        code = mnemonic switch
        {
            "jo" => is64Bit ? Code.Jo_rel32_64 : Code.Jo_rel32_32,
            "jno" => is64Bit ? Code.Jno_rel32_64 : Code.Jno_rel32_32,
            "jb" or "jc" or "jnae" => is64Bit ? Code.Jb_rel32_64 : Code.Jb_rel32_32,
            "jae" or "jnb" or "jnc" => is64Bit ? Code.Jae_rel32_64 : Code.Jae_rel32_32,
            "je" or "jz" => is64Bit ? Code.Je_rel32_64 : Code.Je_rel32_32,
            "jne" or "jnz" => is64Bit ? Code.Jne_rel32_64 : Code.Jne_rel32_32,
            "jbe" or "jna" => is64Bit ? Code.Jbe_rel32_64 : Code.Jbe_rel32_32,
            "ja" or "jnbe" => is64Bit ? Code.Ja_rel32_64 : Code.Ja_rel32_32,
            "js" => is64Bit ? Code.Js_rel32_64 : Code.Js_rel32_32,
            "jns" => is64Bit ? Code.Jns_rel32_64 : Code.Jns_rel32_32,
            "jp" or "jpe" => is64Bit ? Code.Jp_rel32_64 : Code.Jp_rel32_32,
            "jnp" or "jpo" => is64Bit ? Code.Jnp_rel32_64 : Code.Jnp_rel32_32,
            "jl" or "jnge" => is64Bit ? Code.Jl_rel32_64 : Code.Jl_rel32_32,
            "jge" or "jnl" => is64Bit ? Code.Jge_rel32_64 : Code.Jge_rel32_32,
            "jle" or "jng" => is64Bit ? Code.Jle_rel32_64 : Code.Jle_rel32_32,
            "jg" or "jnle" => is64Bit ? Code.Jg_rel32_64 : Code.Jg_rel32_32,
            _ => Code.INVALID
        };
        return code != Code.INVALID;
    }

    private static Code BinaryRegisterRegisterCode(BinaryKind kind, int size) =>
        (kind, size) switch
        {
            (BinaryKind.Add, 1) => Code.Add_rm8_r8,
            (BinaryKind.Add, 2) => Code.Add_rm16_r16,
            (BinaryKind.Add, 8) => Code.Add_rm64_r64,
            (BinaryKind.Add, _) => Code.Add_rm32_r32,
            (BinaryKind.Sub, 1) => Code.Sub_rm8_r8,
            (BinaryKind.Sub, 2) => Code.Sub_rm16_r16,
            (BinaryKind.Sub, 8) => Code.Sub_rm64_r64,
            (BinaryKind.Sub, _) => Code.Sub_rm32_r32,
            (BinaryKind.And, 1) => Code.And_rm8_r8,
            (BinaryKind.And, 2) => Code.And_rm16_r16,
            (BinaryKind.And, 8) => Code.And_rm64_r64,
            (BinaryKind.And, _) => Code.And_rm32_r32,
            (BinaryKind.Or, 1) => Code.Or_rm8_r8,
            (BinaryKind.Or, 2) => Code.Or_rm16_r16,
            (BinaryKind.Or, 8) => Code.Or_rm64_r64,
            (BinaryKind.Or, _) => Code.Or_rm32_r32,
            (BinaryKind.Xor, 1) => Code.Xor_rm8_r8,
            (BinaryKind.Xor, 2) => Code.Xor_rm16_r16,
            (BinaryKind.Xor, 8) => Code.Xor_rm64_r64,
            (BinaryKind.Xor, _) => Code.Xor_rm32_r32,
            (BinaryKind.Cmp, 1) => Code.Cmp_rm8_r8,
            (BinaryKind.Cmp, 2) => Code.Cmp_rm16_r16,
            (BinaryKind.Cmp, 8) => Code.Cmp_rm64_r64,
            (BinaryKind.Cmp, _) => Code.Cmp_rm32_r32,
            (BinaryKind.Test, 1) => Code.Test_rm8_r8,
            (BinaryKind.Test, 2) => Code.Test_rm16_r16,
            (BinaryKind.Test, 8) => Code.Test_rm64_r64,
            (BinaryKind.Test, _) => Code.Test_rm32_r32,
            (BinaryKind.Adc, 1) => Code.Adc_rm8_r8,
            (BinaryKind.Adc, 2) => Code.Adc_rm16_r16,
            (BinaryKind.Adc, 8) => Code.Adc_rm64_r64,
            (BinaryKind.Adc, _) => Code.Adc_rm32_r32,
            (BinaryKind.Sbb, 1) => Code.Sbb_rm8_r8,
            (BinaryKind.Sbb, 2) => Code.Sbb_rm16_r16,
            (BinaryKind.Sbb, 8) => Code.Sbb_rm64_r64,
            (BinaryKind.Sbb, _) => Code.Sbb_rm32_r32,
            _ => Code.INVALID
        };

    private static Code BinaryRegisterMemoryCode(BinaryKind kind, int size) =>
        (kind, size) switch
        {
            (BinaryKind.Add, 1) => Code.Add_r8_rm8,
            (BinaryKind.Add, 2) => Code.Add_r16_rm16,
            (BinaryKind.Add, 8) => Code.Add_r64_rm64,
            (BinaryKind.Add, _) => Code.Add_r32_rm32,
            (BinaryKind.Sub, 1) => Code.Sub_r8_rm8,
            (BinaryKind.Sub, 2) => Code.Sub_r16_rm16,
            (BinaryKind.Sub, 8) => Code.Sub_r64_rm64,
            (BinaryKind.Sub, _) => Code.Sub_r32_rm32,
            (BinaryKind.And, 1) => Code.And_r8_rm8,
            (BinaryKind.And, 2) => Code.And_r16_rm16,
            (BinaryKind.And, 8) => Code.And_r64_rm64,
            (BinaryKind.And, _) => Code.And_r32_rm32,
            (BinaryKind.Or, 1) => Code.Or_r8_rm8,
            (BinaryKind.Or, 2) => Code.Or_r16_rm16,
            (BinaryKind.Or, 8) => Code.Or_r64_rm64,
            (BinaryKind.Or, _) => Code.Or_r32_rm32,
            (BinaryKind.Xor, 1) => Code.Xor_r8_rm8,
            (BinaryKind.Xor, 2) => Code.Xor_r16_rm16,
            (BinaryKind.Xor, 8) => Code.Xor_r64_rm64,
            (BinaryKind.Xor, _) => Code.Xor_r32_rm32,
            (BinaryKind.Cmp, 1) => Code.Cmp_r8_rm8,
            (BinaryKind.Cmp, 2) => Code.Cmp_r16_rm16,
            (BinaryKind.Cmp, 8) => Code.Cmp_r64_rm64,
            (BinaryKind.Cmp, _) => Code.Cmp_r32_rm32,
            (BinaryKind.Test, 1) => Code.Test_rm8_r8,
            (BinaryKind.Test, 2) => Code.Test_rm16_r16,
            (BinaryKind.Test, 8) => Code.Test_rm64_r64,
            (BinaryKind.Test, _) => Code.Test_rm32_r32,
            (BinaryKind.Adc, 1) => Code.Adc_r8_rm8,
            (BinaryKind.Adc, 2) => Code.Adc_r16_rm16,
            (BinaryKind.Adc, 8) => Code.Adc_r64_rm64,
            (BinaryKind.Adc, _) => Code.Adc_r32_rm32,
            (BinaryKind.Sbb, 1) => Code.Sbb_r8_rm8,
            (BinaryKind.Sbb, 2) => Code.Sbb_r16_rm16,
            (BinaryKind.Sbb, 8) => Code.Sbb_r64_rm64,
            (BinaryKind.Sbb, _) => Code.Sbb_r32_rm32,
            _ => Code.INVALID
        };

    private static Code BinaryMemoryRegisterCode(BinaryKind kind, int size) =>
        (kind, size) switch
        {
            (BinaryKind.Add, 1) => Code.Add_rm8_r8,
            (BinaryKind.Add, 2) => Code.Add_rm16_r16,
            (BinaryKind.Add, 8) => Code.Add_rm64_r64,
            (BinaryKind.Add, _) => Code.Add_rm32_r32,
            (BinaryKind.Sub, 1) => Code.Sub_rm8_r8,
            (BinaryKind.Sub, 2) => Code.Sub_rm16_r16,
            (BinaryKind.Sub, 8) => Code.Sub_rm64_r64,
            (BinaryKind.Sub, _) => Code.Sub_rm32_r32,
            (BinaryKind.And, 1) => Code.And_rm8_r8,
            (BinaryKind.And, 2) => Code.And_rm16_r16,
            (BinaryKind.And, 8) => Code.And_rm64_r64,
            (BinaryKind.And, _) => Code.And_rm32_r32,
            (BinaryKind.Or, 1) => Code.Or_rm8_r8,
            (BinaryKind.Or, 2) => Code.Or_rm16_r16,
            (BinaryKind.Or, 8) => Code.Or_rm64_r64,
            (BinaryKind.Or, _) => Code.Or_rm32_r32,
            (BinaryKind.Xor, 1) => Code.Xor_rm8_r8,
            (BinaryKind.Xor, 2) => Code.Xor_rm16_r16,
            (BinaryKind.Xor, 8) => Code.Xor_rm64_r64,
            (BinaryKind.Xor, _) => Code.Xor_rm32_r32,
            (BinaryKind.Cmp, 1) => Code.Cmp_rm8_r8,
            (BinaryKind.Cmp, 2) => Code.Cmp_rm16_r16,
            (BinaryKind.Cmp, 8) => Code.Cmp_rm64_r64,
            (BinaryKind.Cmp, _) => Code.Cmp_rm32_r32,
            (BinaryKind.Test, 1) => Code.Test_rm8_r8,
            (BinaryKind.Test, 2) => Code.Test_rm16_r16,
            (BinaryKind.Test, 8) => Code.Test_rm64_r64,
            (BinaryKind.Test, _) => Code.Test_rm32_r32,
            (BinaryKind.Adc, 1) => Code.Adc_rm8_r8,
            (BinaryKind.Adc, 2) => Code.Adc_rm16_r16,
            (BinaryKind.Adc, 8) => Code.Adc_rm64_r64,
            (BinaryKind.Adc, _) => Code.Adc_rm32_r32,
            (BinaryKind.Sbb, 1) => Code.Sbb_rm8_r8,
            (BinaryKind.Sbb, 2) => Code.Sbb_rm16_r16,
            (BinaryKind.Sbb, 8) => Code.Sbb_rm64_r64,
            (BinaryKind.Sbb, _) => Code.Sbb_rm32_r32,
            _ => Code.INVALID
        };

    private static Code SelectImmediateCode(
        BinaryKind kind,
        int size,
        long immediate,
        bool registerDestination)
    {
        _ = registerDestination;
        bool byteImmediate = immediate is >= sbyte.MinValue and <= sbyte.MaxValue;
        return (kind, size, byteImmediate) switch
        {
            (BinaryKind.Add, 1, _) => Code.Add_rm8_imm8,
            (BinaryKind.Add, 2, _) => Code.Add_rm16_imm16,
            (BinaryKind.Add, 8, true) => Code.Add_rm64_imm8,
            (BinaryKind.Add, 8, false) => Code.Add_rm64_imm32,
            (BinaryKind.Add, _, true) => Code.Add_rm32_imm8,
            (BinaryKind.Add, _, false) => Code.Add_rm32_imm32,
            (BinaryKind.Sub, 1, _) => Code.Sub_rm8_imm8,
            (BinaryKind.Sub, 2, _) => Code.Sub_rm16_imm16,
            (BinaryKind.Sub, 8, true) => Code.Sub_rm64_imm8,
            (BinaryKind.Sub, 8, false) => Code.Sub_rm64_imm32,
            (BinaryKind.Sub, _, true) => Code.Sub_rm32_imm8,
            (BinaryKind.Sub, _, false) => Code.Sub_rm32_imm32,
            (BinaryKind.And, 1, _) => Code.And_rm8_imm8,
            (BinaryKind.And, 2, _) => Code.And_rm16_imm16,
            (BinaryKind.And, 8, true) => Code.And_rm64_imm8,
            (BinaryKind.And, 8, false) => Code.And_rm64_imm32,
            (BinaryKind.And, _, true) => Code.And_rm32_imm8,
            (BinaryKind.And, _, false) => Code.And_rm32_imm32,
            (BinaryKind.Or, 1, _) => Code.Or_rm8_imm8,
            (BinaryKind.Or, 2, _) => Code.Or_rm16_imm16,
            (BinaryKind.Or, 8, true) => Code.Or_rm64_imm8,
            (BinaryKind.Or, 8, false) => Code.Or_rm64_imm32,
            (BinaryKind.Or, _, true) => Code.Or_rm32_imm8,
            (BinaryKind.Or, _, false) => Code.Or_rm32_imm32,
            (BinaryKind.Xor, 1, _) => Code.Xor_rm8_imm8,
            (BinaryKind.Xor, 2, _) => Code.Xor_rm16_imm16,
            (BinaryKind.Xor, 8, true) => Code.Xor_rm64_imm8,
            (BinaryKind.Xor, 8, false) => Code.Xor_rm64_imm32,
            (BinaryKind.Xor, _, true) => Code.Xor_rm32_imm8,
            (BinaryKind.Xor, _, false) => Code.Xor_rm32_imm32,
            (BinaryKind.Cmp, 1, _) => Code.Cmp_rm8_imm8,
            (BinaryKind.Cmp, 2, _) => Code.Cmp_rm16_imm16,
            (BinaryKind.Cmp, 8, true) => Code.Cmp_rm64_imm8,
            (BinaryKind.Cmp, 8, false) => Code.Cmp_rm64_imm32,
            (BinaryKind.Cmp, _, true) => Code.Cmp_rm32_imm8,
            (BinaryKind.Cmp, _, false) => Code.Cmp_rm32_imm32,
            (BinaryKind.Test, 1, _) => Code.Test_rm8_imm8,
            (BinaryKind.Test, 2, _) => Code.Test_rm16_imm16,
            (BinaryKind.Test, 8, _) => Code.Test_rm64_imm32,
            (BinaryKind.Test, _, _) => Code.Test_rm32_imm32,
            (BinaryKind.Adc, 1, _) => Code.Adc_rm8_imm8,
            (BinaryKind.Adc, 2, _) => Code.Adc_rm16_imm16,
            (BinaryKind.Adc, 8, true) => Code.Adc_rm64_imm8,
            (BinaryKind.Adc, 8, false) => Code.Adc_rm64_imm32,
            (BinaryKind.Adc, _, true) => Code.Adc_rm32_imm8,
            (BinaryKind.Adc, _, false) => Code.Adc_rm32_imm32,
            (BinaryKind.Sbb, 1, _) => Code.Sbb_rm8_imm8,
            (BinaryKind.Sbb, 2, _) => Code.Sbb_rm16_imm16,
            (BinaryKind.Sbb, 8, true) => Code.Sbb_rm64_imm8,
            (BinaryKind.Sbb, 8, false) => Code.Sbb_rm64_imm32,
            (BinaryKind.Sbb, _, true) => Code.Sbb_rm32_imm8,
            (BinaryKind.Sbb, _, false) => Code.Sbb_rm32_imm32,
            _ => Code.INVALID
        };
    }

    private static Code UnaryMemoryCode(UnaryKind kind, int size) =>
        (kind, size) switch
        {
            (UnaryKind.Inc, 1) => Code.Inc_rm8,
            (UnaryKind.Inc, 2) => Code.Inc_rm16,
            (UnaryKind.Inc, 8) => Code.Inc_rm64,
            (UnaryKind.Inc, _) => Code.Inc_rm32,
            (UnaryKind.Dec, 1) => Code.Dec_rm8,
            (UnaryKind.Dec, 2) => Code.Dec_rm16,
            (UnaryKind.Dec, 8) => Code.Dec_rm64,
            (UnaryKind.Dec, _) => Code.Dec_rm32,
            (UnaryKind.Not, 1) => Code.Not_rm8,
            (UnaryKind.Not, 2) => Code.Not_rm16,
            (UnaryKind.Not, 8) => Code.Not_rm64,
            (UnaryKind.Not, _) => Code.Not_rm32,
            (UnaryKind.Neg, 1) => Code.Neg_rm8,
            (UnaryKind.Neg, 2) => Code.Neg_rm16,
            (UnaryKind.Neg, 8) => Code.Neg_rm64,
            (UnaryKind.Neg, _) => Code.Neg_rm32,
            (UnaryKind.Mul, 1) => Code.Mul_rm8,
            (UnaryKind.Mul, 2) => Code.Mul_rm16,
            (UnaryKind.Mul, 8) => Code.Mul_rm64,
            (UnaryKind.Mul, _) => Code.Mul_rm32,
            (UnaryKind.Div, 1) => Code.Div_rm8,
            (UnaryKind.Div, 2) => Code.Div_rm16,
            (UnaryKind.Div, 8) => Code.Div_rm64,
            (UnaryKind.Div, _) => Code.Div_rm32,
            (UnaryKind.IDiv, 1) => Code.Idiv_rm8,
            (UnaryKind.IDiv, 2) => Code.Idiv_rm16,
            (UnaryKind.IDiv, 8) => Code.Idiv_rm64,
            (UnaryKind.IDiv, _) => Code.Idiv_rm32,
            _ => Code.INVALID
        };

    private static Code ShiftRegisterImmediateCode(ShiftKind kind, int size) =>
        (kind, size) switch
        {
            (ShiftKind.Shl, 1) => Code.Shl_rm8_imm8,
            (ShiftKind.Shl, 2) => Code.Shl_rm16_imm8,
            (ShiftKind.Shl, 8) => Code.Shl_rm64_imm8,
            (ShiftKind.Shl, _) => Code.Shl_rm32_imm8,
            (ShiftKind.Shr, 1) => Code.Shr_rm8_imm8,
            (ShiftKind.Shr, 2) => Code.Shr_rm16_imm8,
            (ShiftKind.Shr, 8) => Code.Shr_rm64_imm8,
            (ShiftKind.Shr, _) => Code.Shr_rm32_imm8,
            (ShiftKind.Sar, 1) => Code.Sar_rm8_imm8,
            (ShiftKind.Sar, 2) => Code.Sar_rm16_imm8,
            (ShiftKind.Sar, 8) => Code.Sar_rm64_imm8,
            (ShiftKind.Sar, _) => Code.Sar_rm32_imm8,
            (ShiftKind.Rol, 1) => Code.Rol_rm8_imm8,
            (ShiftKind.Rol, 2) => Code.Rol_rm16_imm8,
            (ShiftKind.Rol, 8) => Code.Rol_rm64_imm8,
            (ShiftKind.Rol, _) => Code.Rol_rm32_imm8,
            (ShiftKind.Ror, 1) => Code.Ror_rm8_imm8,
            (ShiftKind.Ror, 2) => Code.Ror_rm16_imm8,
            (ShiftKind.Ror, 8) => Code.Ror_rm64_imm8,
            (ShiftKind.Ror, _) => Code.Ror_rm32_imm8,
            (ShiftKind.Rcl, 1) => Code.Rcl_rm8_imm8,
            (ShiftKind.Rcl, 2) => Code.Rcl_rm16_imm8,
            (ShiftKind.Rcl, 8) => Code.Rcl_rm64_imm8,
            (ShiftKind.Rcl, _) => Code.Rcl_rm32_imm8,
            (ShiftKind.Rcr, 1) => Code.Rcr_rm8_imm8,
            (ShiftKind.Rcr, 2) => Code.Rcr_rm16_imm8,
            (ShiftKind.Rcr, 8) => Code.Rcr_rm64_imm8,
            (ShiftKind.Rcr, _) => Code.Rcr_rm32_imm8,
            _ => Code.INVALID
        };

    private static Code ShiftRegisterClCode(ShiftKind kind, int size) =>
        (kind, size) switch
        {
            (ShiftKind.Shl, 1) => Code.Shl_rm8_CL,
            (ShiftKind.Shl, 2) => Code.Shl_rm16_CL,
            (ShiftKind.Shl, 8) => Code.Shl_rm64_CL,
            (ShiftKind.Shl, _) => Code.Shl_rm32_CL,
            (ShiftKind.Shr, 1) => Code.Shr_rm8_CL,
            (ShiftKind.Shr, 2) => Code.Shr_rm16_CL,
            (ShiftKind.Shr, 8) => Code.Shr_rm64_CL,
            (ShiftKind.Shr, _) => Code.Shr_rm32_CL,
            (ShiftKind.Sar, 1) => Code.Sar_rm8_CL,
            (ShiftKind.Sar, 2) => Code.Sar_rm16_CL,
            (ShiftKind.Sar, 8) => Code.Sar_rm64_CL,
            (ShiftKind.Sar, _) => Code.Sar_rm32_CL,
            (ShiftKind.Rol, 1) => Code.Rol_rm8_CL,
            (ShiftKind.Rol, 2) => Code.Rol_rm16_CL,
            (ShiftKind.Rol, 8) => Code.Rol_rm64_CL,
            (ShiftKind.Rol, _) => Code.Rol_rm32_CL,
            (ShiftKind.Ror, 1) => Code.Ror_rm8_CL,
            (ShiftKind.Ror, 2) => Code.Ror_rm16_CL,
            (ShiftKind.Ror, 8) => Code.Ror_rm64_CL,
            (ShiftKind.Ror, _) => Code.Ror_rm32_CL,
            (ShiftKind.Rcl, 1) => Code.Rcl_rm8_CL,
            (ShiftKind.Rcl, 2) => Code.Rcl_rm16_CL,
            (ShiftKind.Rcl, 8) => Code.Rcl_rm64_CL,
            (ShiftKind.Rcl, _) => Code.Rcl_rm32_CL,
            (ShiftKind.Rcr, 1) => Code.Rcr_rm8_CL,
            (ShiftKind.Rcr, 2) => Code.Rcr_rm16_CL,
            (ShiftKind.Rcr, 8) => Code.Rcr_rm64_CL,
            (ShiftKind.Rcr, _) => Code.Rcr_rm32_CL,
            _ => Code.INVALID
        };

    private static Code ShiftMemoryImmediateCode(ShiftKind kind, int size) =>
        ShiftRegisterImmediateCode(kind, size);

    private static Code ShiftMemoryClCode(ShiftKind kind, int size) =>
        ShiftRegisterClCode(kind, size);

    private static (Register Target, Register Source) NormalizePair(
        Register destination,
        Register source,
        int size)
    {
        Register target = ResizeRegister(destination, size);
        Register normalizedSource = ResizeRegister(source, size);
        return (target, normalizedSource);
    }

    private static Register ResizeRegister(Register register, int size)
    {
        if (size == RegisterSize(register))
        {
            return register;
        }

        string name = register.ToString().ToLowerInvariant();
        string baseName = name.Length > 0 && name[0] == 'r' && name is not "rip"
            ? name
            : name switch
            {
                "al" or "ah" or "ax" or "eax" or "rax" => "rax",
                "bl" or "bh" or "bx" or "ebx" or "rbx" => "rbx",
                "cl" or "ch" or "cx" or "ecx" or "rcx" => "rcx",
                "dl" or "dh" or "dx" or "edx" or "rdx" => "rdx",
                "sil" or "si" or "esi" or "rsi" => "rsi",
                "dil" or "di" or "edi" or "rdi" => "rdi",
                "spl" or "sp" or "esp" or "rsp" => "rsp",
                "bpl" or "bp" or "ebp" or "rbp" => "rbp",
                _ => name
            };

        if (baseName.Length == 0 || baseName[0] != 'r')
        {
            return register;
        }

        string candidate = baseName switch
        {
            "rax" or "rbx" or "rcx" or "rdx" or "rsi" or "rdi" or "rsp" or "rbp" =>
                baseName,
            _ => baseName
        };
        if (size == 8 &&
            Enum.TryParse(candidate, ignoreCase: true, out Register sized))
        {
            return sized;
        }

        if (baseName.Length >= 2 &&
            baseName[0] == 'r' &&
            int.TryParse(baseName[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
        {
            string sizedName = size switch
            {
                1 => $"r{number}b",
                2 => $"r{number}w",
                4 => $"r{number}d",
                _ => $"r{number}"
            };
            if (Enum.TryParse(sizedName, ignoreCase: true, out Register register64))
            {
                return register64;
            }
        }

        string alias = (size, baseName) switch
        {
            (1, "rax") => "al",
            (2, "rax") => "ax",
            (4, "rax") => "eax",
            (1, "rbx") => "bl",
            (2, "rbx") => "bx",
            (4, "rbx") => "ebx",
            (1, "rcx") => "cl",
            (2, "rcx") => "cx",
            (4, "rcx") => "ecx",
            (1, "rdx") => "dl",
            (2, "rdx") => "dx",
            (4, "rdx") => "edx",
            (1, "rsi") => "sil",
            (2, "rsi") => "si",
            (4, "rsi") => "esi",
            (1, "rdi") => "dil",
            (2, "rdi") => "di",
            (4, "rdi") => "edi",
            (1, "rsp") => "spl",
            (2, "rsp") => "sp",
            (4, "rsp") => "esp",
            (1, "rbp") => "bpl",
            (2, "rbp") => "bp",
            (4, "rbp") => "ebp",
            _ => candidate
        };
        return Enum.TryParse(alias, ignoreCase: true, out Register sizedRegister)
            ? sizedRegister
            : register;
    }

    private static int RegisterSize(Register register) => register.GetInfo().Size switch
    {
        1 => 1,
        2 => 2,
        4 => 4,
        8 => 8,
        _ => 8
    };

    private static int OperandSizeHint(string operand, int fallback)
    {
        string lowered = operand.ToLowerInvariant();
        if (lowered.Contains("byte ptr") || lowered.Contains("byte ["))
        {
            return 1;
        }

        if (lowered.Contains("word ptr") || lowered.Contains("word ["))
        {
            return 2;
        }

        if (lowered.Contains("dword ptr") || lowered.Contains("dword ["))
        {
            return 4;
        }

        if (lowered.Contains("qword ptr") || lowered.Contains("qword ["))
        {
            return 8;
        }

        return fallback;
    }

    private static bool TryParseRegister(string operand, out Register register)
    {
        register = Register.None;
        string text = operand.Trim().ToLowerInvariant();
        foreach (string prefix in new[] { "byte ptr ", "word ptr ", "dword ptr ", "qword ptr " })
        {
            if (text.StartsWith(prefix, StringComparison.Ordinal))
            {
                text = text[prefix.Length..].Trim();
            }
        }

        if (text.Length == 0 || text.StartsWith('['))
        {
            return false;
        }

        if (char.IsLetter(text[0]) &&
            Enum.TryParse(text, ignoreCase: true, out register) &&
            register is not Register.None)
        {
            return true;
        }

        register = Register.None;
        return false;
    }

    private static MemoryOperand ParseMemory(string operand)
    {
        string text = operand.Trim();
        Register segment = Register.None;
        int colon = text.IndexOf(':');
        if (colon > 0 && text.StartsWith('[') is false)
        {
            string prefix = text[..colon].Trim();
            if (prefix.Equals("fs", StringComparison.OrdinalIgnoreCase))
            {
                segment = Register.FS;
            }
            else if (prefix.Equals("gs", StringComparison.OrdinalIgnoreCase))
            {
                segment = Register.GS;
            }

            text = text[(colon + 1)..].Trim();
        }

        if (!text.StartsWith('[') || !text.EndsWith(']'))
        {
            throw new AutoAssemblerException($"无法解析内存操作数 '{operand}'");
        }

        string inner = text[1..^1].Trim();
        if (inner.Length == 0)
        {
            throw new AutoAssemblerException($"无法解析内存操作数 '{operand}'");
        }

        Register baseRegister = Register.None;
        Register indexRegister = Register.None;
        int scale = 1;
        long displacement = 0;
        string normalized = inner.Replace("-", "+-").Replace(" +", "+").Replace("+ ", "+");
        foreach (string rawTerm in normalized.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            string term = rawTerm.Trim();
            if (term.Length == 0)
            {
                continue;
            }

            int star = term.IndexOf('*');
            if (star > 0)
            {
                string indexName = term[..star].Trim();
                string scaleText = term[(star + 1)..].Trim();
                if (!TryParseRegister(indexName, out Register parsedIndex))
                {
                    throw new AutoAssemblerException($"无法解析索引寄存器 '{indexName}'");
                }

                indexRegister = parsedIndex;
                scale = int.Parse(scaleText, CultureInfo.InvariantCulture);
                if (scale is not (1 or 2 or 4 or 8))
                {
                    throw new AutoAssemblerException($"无效的比例因子 '{scaleText}'");
                }

                continue;
            }

            if (TryParseRegister(term, out Register parsedRegister))
            {
                if (baseRegister == Register.None)
                {
                    baseRegister = parsedRegister;
                }
                else if (indexRegister == Register.None)
                {
                    indexRegister = parsedRegister;
                }

                continue;
            }

            displacement += unchecked((long)ParseNumber(term));
        }

        if (baseRegister == Register.None && indexRegister == Register.None)
        {
            return new MemoryOperand(unchecked((ulong)displacement), 8);
        }

        return new MemoryOperand(baseRegister, indexRegister, scale, displacement, 0, false, segment);
    }

    private static ulong ParseNumber(string text)
    {
        string value = text.Trim();
        foreach (string prefix in new[] { "byte ptr ", "word ptr ", "dword ptr", "qword ptr", "near ptr", "short" })
        {
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                value = value[prefix.Length..].Trim();
            }
        }

        if (value.Length == 0)
        {
            throw new AutoAssemblerException("缺少立即数");
        }

        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
        {
            return value.Length == 3 ? value[1] : 0UL;
        }

        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            ulong.TryParse(
                value[2..],
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out ulong hexadecimal))
        {
            return hexadecimal;
        }

        if (value.StartsWith('$') &&
            ulong.TryParse(
                value[1..],
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out ulong dollarHex))
        {
            return dollarHex;
        }

        if (value.EndsWith('h') || value.EndsWith('H'))
        {
            string digits = value[..^1];
            if (digits.Length > 0 &&
                digits.All(static character =>
                    Uri.IsHexDigit(character)) &&
                ulong.TryParse(
                    digits,
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out ulong suffixedHex))
            {
                return suffixedHex;
            }
        }

        if (value.StartsWith('-') &&
            long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long negative))
        {
            return unchecked((ulong)negative);
        }

        if (ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong decimalValue))
        {
            return decimalValue;
        }

        throw new AutoAssemblerException($"无法解析数字 '{text}'");
    }

    private static string[] SplitOperands(string operandText)
    {
        if (string.IsNullOrWhiteSpace(operandText))
        {
            return [];
        }

        List<string> operands = [];
        int depth = 0;
        int start = 0;
        for (int index = 0; index < operandText.Length; index++)
        {
            char current = operandText[index];
            if (current == '[')
            {
                depth++;
            }
            else if (current == ']')
            {
                depth--;
            }
            else if (current == ',' && depth == 0)
            {
                operands.Add(operandText[start..index].Trim());
                start = index + 1;
            }
        }

        operands.Add(operandText[start..].Trim());
        return operands.ToArray();
    }

    private enum BinaryKind
    {
        Add,
        Sub,
        And,
        Or,
        Xor,
        Cmp,
        Test,
        Adc,
        Sbb
    }

    private enum UnaryKind
    {
        Inc,
        Dec,
        Not,
        Neg,
        Mul,
        Div,
        IDiv
    }

    private enum ShiftKind
    {
        Shl,
        Shr,
        Sar,
        Rol,
        Ror,
        Rcl,
        Rcr
    }

    private sealed class WrittenBytes : CodeWriter
    {
        private readonly List<byte> _bytes = [];

        public override void WriteByte(byte value) => _bytes.Add(value);

        public byte[] ToArray() => [.. _bytes];
    }
}
