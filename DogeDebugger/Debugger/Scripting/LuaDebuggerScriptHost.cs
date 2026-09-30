using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using DogeDebugger.Core.Search;
using DogeDebugger.Debugger.Breakpoints;
using DogeDebugger.Debugger.Session;
using MoonSharp.Interpreter;

namespace DogeDebugger.Debugger.Scripting;

public sealed class LuaDebuggerScriptHost
{
    private readonly DebuggerSession _session;
    private readonly StringBuilder _output = new();
    private readonly object _sync = new();
    private Script? _activeScript;

    public LuaDebuggerScriptHost(DebuggerSession session)
    {
        _session = session;
    }

    public async Task<LuaScriptExecutionResult> ExecuteAsync(
        string source,
        CancellationToken cancellationToken = default)
    {
        LuaScriptExecutionResult startResult = await StartAsync(source, cancellationToken)
            .ConfigureAwait(false);
        if (!startResult.Succeeded)
        {
            return startResult;
        }

        LuaScriptExecutionResult stopResult = await StopAsync(cancellationToken)
            .ConfigureAwait(false);
        return new LuaScriptExecutionResult
        {
            Succeeded = stopResult.Succeeded,
            OutputText = startResult.OutputText + stopResult.OutputText,
            ErrorText = stopResult.ErrorText,
            Duration = startResult.Duration + stopResult.Duration
        };
    }

    public async Task<LuaScriptExecutionResult> StartAsync(
        string source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        Script script;
        lock (_sync)
        {
            if (_activeScript is not null)
            {
                return new LuaScriptExecutionResult
                {
                    Succeeded = false,
                    OutputText = string.Empty,
                    ErrorText = "A Lua script is already running.",
                    Duration = TimeSpan.Zero
                };
            }

            _output.Clear();
            script = new Script(CoreModules.Preset_HardSandbox);
            RegisterApi(script);
            _activeScript = script;
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            await Task.Run(
                () =>
                {
                    script.DoString(source);
                    InvokeLifecycle(script, "OnStart");
                },
                cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            return new LuaScriptExecutionResult
            {
                Succeeded = true,
                OutputText = _output.ToString(),
                ErrorText = string.Empty,
                Duration = stopwatch.Elapsed
            };
        }
        catch (Exception exception)
        {
            lock (_sync)
            {
                if (ReferenceEquals(_activeScript, script))
                {
                    _activeScript = null;
                }
            }

            stopwatch.Stop();
            return new LuaScriptExecutionResult
            {
                Succeeded = false,
                OutputText = _output.ToString(),
                ErrorText = exception.Message,
                Duration = stopwatch.Elapsed
            };
        }
    }

    public async Task<LuaScriptExecutionResult> StopAsync(
        CancellationToken cancellationToken = default)
    {
        Script? script;
        lock (_sync)
        {
            script = _activeScript;
        }

        if (script is null)
        {
            return new LuaScriptExecutionResult
            {
                Succeeded = true,
                OutputText = string.Empty,
                ErrorText = string.Empty,
                Duration = TimeSpan.Zero
            };
        }

        string initialOutput;
        lock (_sync)
        {
            initialOutput = _output.ToString();
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            await Task.Run(
                () => InvokeLifecycle(script, "OnEnd"),
                cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            lock (_sync)
            {
                if (ReferenceEquals(_activeScript, script))
                {
                    _activeScript = null;
                }
            }

            string output = _output.ToString();
            return new LuaScriptExecutionResult
            {
                Succeeded = true,
                OutputText = output.StartsWith(initialOutput, StringComparison.Ordinal)
                    ? output[initialOutput.Length..]
                    : output,
                ErrorText = string.Empty,
                Duration = stopwatch.Elapsed
            };
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            return new LuaScriptExecutionResult
            {
                Succeeded = false,
                OutputText = _output.ToString(),
                ErrorText = exception.Message,
                Duration = stopwatch.Elapsed
            };
        }
    }

    private void RegisterApi(Script script)
    {
        Table globals = script.Globals;
        globals["print"] = DynValue.NewCallback(
            static (_, args) =>
            {
                if (args.Count > 0)
                {
                    // The per-script output writer is injected below by closure.
                }

                return DynValue.Nil;
            });
        globals["print"] = DynValue.NewCallback((_, args) =>
        {
            _output.AppendLine(string.Join(
                "\t",
                args.GetArray().Select(FormatLuaValue)));
            return DynValue.Nil;
        });
        globals["read_bytes"] = DynValue.NewCallback((_, args) =>
        {
            ulong address = ToUInt64(args[0]);
            int size = ToInt32(args[1]);
            return DynValue.NewString(Convert.ToHexString(_session.ReadBytes(address, size)));
        });
        globals["write_bytes"] = DynValue.NewCallback((_, args) =>
        {
            ulong address = ToUInt64(args[0]);
            byte[] bytes = ParseHex(args[1].CastToString());
            return DynValue.NewBoolean(_session.WriteBytes(address, bytes));
        });
        globals["read_integer"] = DynValue.NewCallback((_, args) =>
        {
            ulong address = ToUInt64(args[0]);
            int size = Math.Clamp(ToInt32(args[1]), 1, 8);
            byte[] bytes = _session.ReadBytes(address, size);
            if (bytes.Length != size)
            {
                return DynValue.Nil;
            }

            ulong value = size switch
            {
                1 => bytes[0],
                2 => BitConverter.ToUInt16(bytes),
                4 => BitConverter.ToUInt32(bytes),
                8 => BitConverter.ToUInt64(bytes),
                _ => FoldLittleEndian(bytes)
            };
            return DynValue.NewNumber(value);
        });
        globals["get_register"] = DynValue.NewCallback((_, args) =>
        {
            string name = args[0].CastToString();
            if (_session.Debugger.GetCurrentRegisters().GeneralPurposeRegisters.TryGetValue(
                    name,
                    out ulong value))
            {
                return DynValue.NewNumber(value);
            }

            return DynValue.Nil;
        });
        globals["get_modules"] = DynValue.NewCallback((_, _) =>
        {
            Table table = new(script);
            int index = 1;
            foreach (DogeDebugger.Core.Modules.ModuleDescriptor module in _session.EnumerateModules())
            {
                table[index++] = new Table(script)
                {
                    ["name"] = module.Name,
                    ["path"] = module.FilePath,
                    ["base"] = module.BaseAddress,
                    ["size"] = module.Size,
                    ["entry"] = module.EntryPoint
                };
            }

            return DynValue.NewTable(table);
        });
        globals["aob_scan"] = DynValue.NewCallback((_, args) =>
        {
            SearchResult result = _session.Search(new SearchRequest
            {
                Pattern = args[0].CastToString(),
                MaximumResults = args.Count > 1 ? ToInt32(args[1]) : 10_000
            });
            Table table = new(script);
            int index = 1;
            foreach (SearchMatch match in result.Matches)
            {
                table[index++] = match.Address;
            }

            return DynValue.NewTable(table);
        });
        globals["disassemble"] = DynValue.NewCallback((_, args) =>
        {
            ulong address = ToUInt64(args[0]);
            int count = args.Count > 1 ? ToInt32(args[1]) : 20;
            Table table = new(script);
            int index = 1;
            foreach (DogeDebugger.Core.Disassembly.InstructionSnapshot instruction in
                     _session.Disassemble(address, count))
            {
                table[index++] = new Table(script)
                {
                    ["address"] = instruction.Address,
                    ["bytes"] = Convert.ToHexString(instruction.Bytes),
                    ["text"] = instruction.Text
                };
            }

            return DynValue.NewTable(table);
        });
        globals["set_breakpoint"] = DynValue.NewCallback((_, args) =>
            DynValue.NewBoolean(_session.AddBreakpoint(ToUInt64(args[0]))));
        globals["remove_breakpoint"] = DynValue.NewCallback((_, args) =>
            DynValue.NewBoolean(_session.RemoveBreakpoint(ToUInt64(args[0]))));
        globals["continue_execution"] = DynValue.NewCallback((_, _) =>
        {
            _ = _session.Debugger.ContinueAsync();
            return DynValue.Nil;
        });
        globals["break_execution"] = DynValue.NewCallback((_, _) =>
        {
            _ = _session.Debugger.BreakAsync();
            return DynValue.Nil;
        });
        globals["step_into"] = DynValue.NewCallback((_, _) =>
        {
            _ = _session.Debugger.StepIntoAsync();
            return DynValue.Nil;
        });
        globals["step_over"] = DynValue.NewCallback((_, _) =>
        {
            _ = _session.Debugger.StepOverAsync();
            return DynValue.Nil;
        });
        globals["step_out"] = DynValue.NewCallback((_, _) =>
        {
            _ = _session.Debugger.StepOutAsync();
            return DynValue.Nil;
        });
        globals["file_append"] = DynValue.NewCallback((_, args) =>
        {
            AppendFile(args[0].CastToString(), args[1].CastToString(), appendLine: false);
            return DynValue.Nil;
        });
        globals["file_appendLine"] = DynValue.NewCallback((_, args) =>
        {
            AppendFile(args[0].CastToString(), args[1].CastToString(), appendLine: true);
            return DynValue.Nil;
        });
    }

    private static void InvokeLifecycle(Script script, string name)
    {
        DynValue function = script.Globals.Get(name);
        if (function.Type == DataType.Function)
        {
            script.Call(function);
        }
    }

    private void AppendFile(string path, string text, bool appendLine)
    {
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.AppendAllText(
            fullPath,
            appendLine ? text + Environment.NewLine : text,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string FormatLuaValue(DynValue value) =>
        value.Type switch
        {
            DataType.Nil => "nil",
            DataType.Boolean => value.Boolean ? "true" : "false",
            DataType.Number => value.Number.ToString("R", CultureInfo.InvariantCulture),
            DataType.String => value.String,
            _ => value.ToDebugPrintString()
        };

    private static ulong ToUInt64(DynValue value)
    {
        if (value.Type == DataType.Number)
        {
            return unchecked((ulong)value.Number);
        }

        string text = value.CastToString().Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return ulong.Parse(
                text[2..],
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture);
        }

        return ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong result)
            ? result
            : ulong.Parse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
    }

    private static int ToInt32(DynValue value) =>
        value.Type == DataType.Number
            ? checked((int)value.Number)
            : int.Parse(value.CastToString(), CultureInfo.InvariantCulture);

    private static byte[] ParseHex(string text)
    {
        string normalized = new(
            text.Where(static character => Uri.IsHexDigit(character))
                .ToArray());
        if (normalized.Length % 2 != 0)
        {
            throw new FormatException("Hex byte text must contain an even number of digits.");
        }

        return Convert.FromHexString(normalized);
    }

    private static ulong FoldLittleEndian(ReadOnlySpan<byte> bytes)
    {
        ulong value = 0;
        for (int index = 0; index < bytes.Length; index++)
        {
            value |= (ulong)bytes[index] << (index * 8);
        }

        return value;
    }
}
