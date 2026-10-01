using System.Globalization;
using System.Text;
using DogeDebugger.Core.Process;
using DogeDebugger.Debugger.UserMode;
using MoonSharp.Interpreter;

namespace DogeDebugger.Debugger.Breakpoints;

public readonly record struct BreakpointConditionResult(
    bool ShouldBreak,
    bool HasError,
    string? ErrorMessage)
{
    public static BreakpointConditionResult Break => new(true, false, null);

    public static BreakpointConditionResult Continue => new(false, false, null);
}

public sealed class BreakpointConditionEvaluator
{
    private readonly ITargetProcess _target;
    private readonly Dictionary<ulong, Dictionary<string, DynValue>> _persistentState = [];

    public BreakpointConditionEvaluator(ITargetProcess target)
    {
        _target = target;
    }

    public static string? ValidateSyntax(string condition)
    {
        if (string.IsNullOrWhiteSpace(condition))
        {
            return null;
        }

        try
        {
            Script script = CreateScript();
            RegisterPlaceholderApi(script);
            try
            {
                script.DoString(BuildExpressionSource(condition));
            }
            catch (SyntaxErrorException)
            {
                script.DoString(condition);
            }

            return null;
        }
        catch (Exception exception)
        {
            return exception.Message;
        }
    }

    public BreakpointConditionResult Evaluate(
        BreakpointEntry breakpoint,
        RegisterSnapshot registers)
    {
        ArgumentNullException.ThrowIfNull(breakpoint);
        if (string.IsNullOrWhiteSpace(breakpoint.Condition))
        {
            return BreakpointConditionResult.Break;
        }

        try
        {
            Script script = CreateScript();
            RegisterRegisters(script, registers);
            RegisterRuntimeApi(script, registers);
            RestorePersistentState(script, breakpoint.Address);

            DynValue result;
            try
            {
                result = script.DoString(BuildExpressionSource(breakpoint.Condition));
            }
            catch (SyntaxErrorException)
            {
                result = script.DoString(breakpoint.Condition);
            }

            CapturePersistentState(script, breakpoint.Address);
            bool shouldBreak = ConvertResultToBoolean(result);
            return shouldBreak
                ? BreakpointConditionResult.Break
                : BreakpointConditionResult.Continue;
        }
        catch (SyntaxErrorException exception)
        {
            return new BreakpointConditionResult(
                ShouldBreak: true,
                HasError: true,
                ErrorMessage: exception.Message);
        }
        catch (ScriptRuntimeException exception)
        {
            return new BreakpointConditionResult(
                ShouldBreak: true,
                HasError: true,
                ErrorMessage: exception.Message);
        }
        catch (Exception exception)
        {
            return new BreakpointConditionResult(
                ShouldBreak: true,
                HasError: true,
                ErrorMessage: exception.Message);
        }
    }

    private static Script CreateScript()
    {
        return new Script(CoreModules.Preset_HardSandbox);
    }

    private static string BuildExpressionSource(string condition)
    {
        return $"return ({condition.Trim()})";
    }

    private static bool ConvertResultToBoolean(DynValue value)
    {
        return value.Type switch
        {
            DataType.Boolean => value.Boolean,
            DataType.Number => Math.Abs(value.Number) > double.Epsilon,
            DataType.Nil or DataType.Void => true,
            _ => true
        };
    }

    private static void RegisterRegisters(Script script, RegisterSnapshot registers)
    {
        foreach ((string name, ulong value) in registers.GeneralPurposeRegisters)
        {
            script.Globals[name] = value;
        }

        script.Globals["RIP"] = registers.InstructionPointer;
        script.Globals["EIP"] = registers.InstructionPointer;
        script.Globals["RSP"] = registers.StackPointer;
        script.Globals["ESP"] = registers.StackPointer;
        script.Globals["RBP"] = registers.FramePointer;
        script.Globals["EBP"] = registers.FramePointer;
        RegisterFlags(script, registers.Flags);
    }

    private static void RegisterFlags(Script script, ulong flags)
    {
        script.Globals["CF"] = (flags >> 0) & 1u;
        script.Globals["PF"] = (flags >> 2) & 1u;
        script.Globals["AF"] = (flags >> 4) & 1u;
        script.Globals["ZF"] = (flags >> 6) & 1u;
        script.Globals["SF"] = (flags >> 7) & 1u;
        script.Globals["TF"] = (flags >> 8) & 1u;
        script.Globals["DF"] = (flags >> 10) & 1u;
        script.Globals["OF"] = (flags >> 11) & 1u;
        script.Globals["EFLAGS"] = flags;
        script.Globals["RFLAGS"] = flags;
    }

    private static void RegisterPlaceholderApi(Script script)
    {
        RegisterReadApi(
            script,
            static (_, size) => new byte[size],
            static _ => 0,
            static _ => string.Empty);
    }

    private void RegisterRuntimeApi(Script script, RegisterSnapshot registers)
    {
        RegisterReadApi(
            script,
            ReadBytes,
            static bytes => bytes.Length == 0 ? 0 : BitConverter.ToUInt64(
                PadLittleEndian(bytes, sizeof(ulong))),
            static bytes => bytes.Length == 0
                ? string.Empty
                : Encoding.UTF8.GetString(bytes).TrimEnd('\0'));

        script.Globals["readPointer"] = DynValue.NewCallback((_, args) =>
        {
            ulong address = ToUInt64(args[0]);
            int size = _target.Is64Bit ? sizeof(ulong) : sizeof(uint);
            byte[] bytes = ReadBytes(address, size);
            if (bytes.Length != size)
            {
                return DynValue.Nil;
            }

            ulong value = size == sizeof(ulong)
                ? BitConverter.ToUInt64(bytes)
                : BitConverter.ToUInt32(bytes);
            return DynValue.NewNumber(value);
        });

        _ = registers;
    }

    private static void RegisterReadApi(
        Script script,
        Func<ulong, int, byte[]> readBytes,
        Func<byte[], ulong> readInteger,
        Func<byte[], string> readString)
    {
        script.Globals["readBytes"] = DynValue.NewCallback((_, args) =>
        {
            ulong address = ToUInt64(args[0]);
            int size = args.Count > 1
                ? Math.Clamp(ToInt32(args[1]), 1, 4096)
                : 1;
            return DynValue.NewString(
                Convert.ToHexString(readBytes(address, size)));
        });

        script.Globals["readInteger"] = DynValue.NewCallback((_, args) =>
        {
            ulong address = ToUInt64(args[0]);
            int size = args.Count > 1
                ? Math.Clamp(ToInt32(args[1]), 1, sizeof(ulong))
                : sizeof(uint);
            byte[] bytes = readBytes(address, size);
            return bytes.Length == size
                ? DynValue.NewNumber(readInteger(bytes))
                : DynValue.Nil;
        });

        script.Globals["readQword"] = DynValue.NewCallback((_, args) =>
        {
            ulong address = ToUInt64(args[0]);
            byte[] bytes = readBytes(address, sizeof(ulong));
            return bytes.Length == sizeof(ulong)
                ? DynValue.NewNumber(BitConverter.ToUInt64(bytes))
                : DynValue.Nil;
        });

        script.Globals["readFloat"] = DynValue.NewCallback((_, args) =>
        {
            ulong address = ToUInt64(args[0]);
            byte[] bytes = readBytes(address, sizeof(float));
            return bytes.Length == sizeof(float)
                ? DynValue.NewNumber(BitConverter.ToSingle(bytes))
                : DynValue.Nil;
        });

        script.Globals["readDouble"] = DynValue.NewCallback((_, args) =>
        {
            ulong address = ToUInt64(args[0]);
            byte[] bytes = readBytes(address, sizeof(double));
            return bytes.Length == sizeof(double)
                ? DynValue.NewNumber(BitConverter.ToDouble(bytes))
                : DynValue.Nil;
        });

        script.Globals["readString"] = DynValue.NewCallback((_, args) =>
        {
            ulong address = ToUInt64(args[0]);
            int size = args.Count > 1
                ? Math.Clamp(ToInt32(args[1]), 1, 4096)
                : 256;
            byte[] bytes = readBytes(address, size);
            return DynValue.NewString(readString(bytes));
        });
    }

    private byte[] ReadBytes(ulong address, int size)
    {
        byte[] bytes = new byte[size];
        return _target.TryReadBytes(address, bytes) ? bytes : [];
    }

    private void RestorePersistentState(Script script, ulong address)
    {
        if (!_persistentState.TryGetValue(address, out Dictionary<string, DynValue>? state))
        {
            return;
        }

        foreach ((string name, DynValue value) in state)
        {
            script.Globals[name] = value;
        }
    }

    private void CapturePersistentState(Script script, ulong address)
    {
        DynValue counter = script.Globals.Get("counter");
        if (counter.Type is DataType.Nil or DataType.Void)
        {
            _persistentState.Remove(address);
            return;
        }

        _persistentState[address] = new Dictionary<string, DynValue>(
            StringComparer.Ordinal)
        {
            ["counter"] = counter
        };
    }

    private static byte[] PadLittleEndian(byte[] value, int size)
    {
        if (value.Length == size)
        {
            return value;
        }

        byte[] padded = new byte[size];
        Array.Copy(value, padded, Math.Min(value.Length, size));
        return padded;
    }

    private static ulong FoldLittleEndian(byte[] bytes)
    {
        ulong value = 0;
        for (int index = 0; index < Math.Min(bytes.Length, sizeof(ulong)); index++)
        {
            value |= (ulong)bytes[index] << (index * 8);
        }

        return value;
    }

    private static ulong ToUInt64(DynValue value)
    {
        return value.Type switch
        {
            DataType.Number => unchecked((ulong)value.Number),
            DataType.String => ulong.TryParse(
                value.String,
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out ulong parsed)
                ? parsed
                : 0,
            _ => 0
        };
    }

    private static int ToInt32(DynValue value)
    {
        return value.Type == DataType.Number
            ? checked((int)value.Number)
            : 0;
    }
}
