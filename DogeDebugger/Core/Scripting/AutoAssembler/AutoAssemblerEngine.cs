using System.Globalization;
using System.IO;
using System.Text;
using DogeDebugger.Core.Disassembly;
using DogeDebugger.Core.Memory;
using DogeDebugger.Core.Modules;
using DogeDebugger.Core.Native;
using DogeDebugger.Core.Process;
using DogeDebugger.Core.Search;
using Iced.Intel;

namespace DogeDebugger.Core.Scripting.AutoAssembler;

/// <summary>
/// Compiles and executes Auto Assembler sections against a live target.
/// The command set, evaluation order, trace log format and error messages
/// reproduce the original engine (H0086FBA in the shipped assembly).
/// </summary>
public sealed class AutoAssemblerEngine
{
    private const uint PageExecuteReadWrite = 0x40;

    private readonly TargetProcess _target;
    private readonly AutoAssemblerSymbolResolver _resolver;
    private readonly MemoryRegionCatalog _regions = new(new ModuleCatalog());
    private readonly List<string> _log = [];

    public AutoAssemblerEngine(
        TargetProcess target,
        AutoAssemblerSymbolResolver resolver)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    /// <summary>
    /// State produced by the last successful enable pass, consumed by the
    /// matching disable pass.
    /// </summary>
    public AutoAssemblerSessionState? ActiveSession { get; private set; }

    public AutoAssemblerResult Execute(string script, bool enable)
    {
        _log.Clear();
        try
        {
            AutoAssemblerCompileContext context = Compile(script, enable, validateOnly: false);
            AutoAssemblerSessionState? disableState = enable
                ? ActiveSession
                : ActiveSession;
            AutoAssemblerSessionState newState = new();
            ExecuteContext(context, enable, disableState, newState);
            if (enable)
            {
                ActiveSession = newState;
            }
            else
            {
                ActiveSession = null;
            }

            return new AutoAssemblerResult
            {
                Success = true,
                Trace = [.. _log],
                DisableState = enable ? newState : null
            };
        }
        catch (AutoAssemblerException exception)
        {
            return new AutoAssemblerResult
            {
                Success = false,
                ErrorMessage = exception.Message,
                ErrorLine = exception.LineNumber,
                Trace = [.. _log]
            };
        }
        catch (Exception exception)
        {
            return new AutoAssemblerResult
            {
                Success = false,
                ErrorMessage = exception.Message,
                ErrorLine = -1,
                Trace = [.. _log]
            };
        }
    }

    /// <summary>
    /// Parses the script and evaluates address-dependent commands without
    /// touching target memory. Mirrors the original validate-only path.
    /// </summary>
    public AutoAssemblerResult Validate(string script)
    {
        _log.Clear();
        try
        {
            _ = Compile(script, enable: true, validateOnly: true);
            return new AutoAssemblerResult { Success = true, Trace = [.. _log] };
        }
        catch (AutoAssemblerException exception)
        {
            return new AutoAssemblerResult
            {
                Success = false,
                ErrorMessage = exception.Message,
                ErrorLine = exception.LineNumber,
                Trace = [.. _log]
            };
        }
        catch (Exception exception)
        {
            return new AutoAssemblerResult
            {
                Success = false,
                ErrorMessage = exception.Message,
                Trace = [.. _log]
            };
        }
    }

    private AutoAssemblerCompileContext Compile(
        string script,
        bool enable,
        bool validateOnly)
    {
        List<AutoAssemblerScriptLine> lines = AutoAssemblerScriptParser.ExtractSection(
            script,
            enable);
        AutoAssemblerScriptParser.StripComments(lines);
        AutoAssemblerScriptParser.RewriteAnonymousLabels(lines);
        HashSet<string> localLabels = AutoAssemblerScriptParser.CollectLocalLabels(lines);
        AutoAssemblerCompileContext context = new() { LocalLabels = localLabels };

        for (int index = 0; index < lines.Count; index++)
        {
            AutoAssemblerScriptLine sourceLine = lines[index];
            string rawText = sourceLine.Text;
            if (rawText.Length == 0 || rawText.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                int lineNumber = sourceLine.LineNumber;
                ProcessLine(
                    context,
                    lines,
                    ref index,
                    rawText,
                    lineNumber,
                    enable,
                    validateOnly);
            }
            catch (AutoAssemblerException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new AutoAssemblerException(
                    exception.Message,
                    sourceLine.LineNumber,
                    rawText);
            }
        }

        return context;
    }

    private void ProcessLine(
        AutoAssemblerCompileContext context,
        List<AutoAssemblerScriptLine> lines,
        ref int index,
        string rawText,
        int lineNumber,
        bool enable,
        bool validateOnly)
    {
        if (IsCall(rawText, "registersymbol"))
        {
            foreach (string name in SplitArguments(GetArguments(rawText, lineNumber)))
            {
                context.RegisteredSymbols.Add(name);
            }

            return;
        }

        if (IsCall(rawText, "unregistersymbol"))
        {
            string argument = GetArguments(rawText, lineNumber).Trim();
            if (argument == "*")
            {
                context.UnregisterAll = true;
            }
            else
            {
                foreach (string name in SplitArguments(argument))
                {
                    context.UnregisteredSymbols.Add(name);
                }
            }

            return;
        }

        if (IsCall(rawText, "define"))
        {
            (string name, string value) = SplitPair(GetArguments(rawText, lineNumber), "define");
            context.Defines.RemoveAll(
                entry => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase));
            context.Defines.Add((name, SubstituteDefines(context, value)));
            return;
        }

        string text = SubstituteDefines(context, rawText);

        if (IsCall(text, "alloc"))
        {
            string[] arguments = SplitCommaArguments(GetArguments(text, lineNumber));
            if (arguments.Length < 2)
            {
                throw new AutoAssemblerException(
                    "alloc 语法: alloc(name, size [, nearAddress])",
                    lineNumber,
                    rawText);
            }

            if (!TryParseSizeNumber(arguments[1], out long sizeValue) || sizeValue <= 0)
            {
                throw new AutoAssemblerException(
                    $"'{arguments[1]}' 不是有效的大小",
                    lineNumber,
                    rawText);
            }

            string? nearAddress = arguments.Length >= 3 && arguments[2].Length > 0
                ? arguments[2]
                : null;
            context.Allocations.Add(new AutoAssemblerAllocationRequest
            {
                Name = arguments[0],
                Size = (int)sizeValue,
                NearAddress = nearAddress
            });
            return;
        }

        if (IsCall(text, "dealloc"))
        {
            string argument = GetArguments(text, lineNumber).Trim();
            if (argument == "*")
            {
                context.DeallocateAll = true;
            }
            else
            {
                foreach (string name in SplitArguments(argument))
                {
                    context.Deallocations.Add(name);
                }
            }

            return;
        }

        if (IsCall(text, "label"))
        {
            foreach (string name in SplitArguments(GetArguments(text, lineNumber)))
            {
                EnsureLabel(context, name);
            }

            return;
        }

        if (IsCall(text, "createthread") && !IsCall(text, "createthreadandwait"))
        {
            context.CreateThreads.Add(GetArguments(text, lineNumber).Trim());
            return;
        }

        if (IsCall(text, "createthreadandwait"))
        {
            string[] arguments = SplitCommaArguments(GetArguments(text, lineNumber));
            int timeout = 0;
            if (arguments.Length >= 2)
            {
                _ = int.TryParse(
                    arguments[1],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out timeout);
            }

            context.CreateThreadsAndWait.Add(new AutoAssemblerThreadRequest
            {
                Name = arguments[0],
                LineIndex = context.AssemblerLines.Count,
                TimeoutMilliseconds = timeout
            });
            return;
        }

        if (TryProcessAobScan(context, text, lineNumber, rawText))
        {
            return;
        }

        if (IsCall(text, "assert"))
        {
            if (validateOnly)
            {
                return;
            }

            ProcessAssert(text, lineNumber, rawText);
            return;
        }

        if (IsCall(text, "readmem"))
        {
            context.AssemblerLines.Add(new AutoAssemblerAssemblerLine(text, lineNumber));
            return;
        }

        if (IsCall(text, "fullaccess"))
        {
            if (validateOnly)
            {
                return;
            }

            ProcessFullAccess(text, lineNumber, rawText);
            return;
        }

        if (IsCall(text, "loadlibrary"))
        {
            if (validateOnly)
            {
                return;
            }

            ProcessLoadLibrary(text, lineNumber, rawText);
            return;
        }

        if (IsCall(text, "include"))
        {
            ProcessInclude(context, lines, ref index, text, lineNumber, rawText);
            return;
        }

        if (text.Contains(':') && TryGetAddressLineName(text, out string candidateName))
        {
            EnsureLabel(context, candidateName);
        }

        context.AssemblerLines.Add(new AutoAssemblerAssemblerLine(text, lineNumber));
    }

    private bool TryProcessAobScan(
        AutoAssemblerCompileContext context,
        string text,
        int lineNumber,
        string rawText)
    {
        if (IsCall(text, "aobscanmodule"))
        {
            string[] arguments = SplitCommaArguments(GetArguments(text, lineNumber), 3);
            if (arguments.Length < 3)
            {
                throw new AutoAssemblerException(
                    "aobscanmodule 语法: aobscanmodule(name, module, pattern)",
                    lineNumber,
                    rawText);
            }

            string symbol = TrimQuotes(arguments[0]);
            string moduleName = TrimQuotes(arguments[1]);
            ModuleDescriptor? module = FindModule(moduleName);
            if (module is null)
            {
                throw new AutoAssemblerException(
                    $"Module '{moduleName}' not found.",
                    lineNumber,
                    rawText);
            }

            if (!TryScanRange(
                    module.BaseAddress,
                    module.BaseAddress + module.Size,
                    arguments[2],
                    out ulong found))
            {
                throw new AutoAssemblerException(
                    $"aobscanmodule: 在模块 '{moduleName}' 中未找到 AOB 模式",
                    lineNumber,
                    rawText);
            }

            context.Defines.Add((symbol, $"0x{found:X}"));
            return true;
        }

        if (IsCall(text, "aobscanregion"))
        {
            string[] arguments = SplitCommaArguments(GetArguments(text, lineNumber), 4);
            if (arguments.Length < 4)
            {
                throw new AutoAssemblerException(
                    "aobscanregion 语法: aobscanregion(name, start, stop, pattern)",
                    lineNumber,
                    rawText);
            }

            string symbol = TrimQuotes(arguments[0]);
            string startText = TrimQuotes(arguments[1]);
            string stopText = TrimQuotes(arguments[2]);
            if (!_resolver.TryResolve(startText, out ulong start))
            {
                throw new AutoAssemblerException(
                    $"aobscanregion: 无法解析起始地址 '{startText}'",
                    lineNumber,
                    rawText);
            }

            if (!_resolver.TryResolve(stopText, out ulong stop))
            {
                throw new AutoAssemblerException(
                    $"aobscanregion: 无法解析结束地址 '{stopText}'",
                    lineNumber,
                    rawText);
            }

            if (!TryScanRange(start, stop, arguments[3], out ulong found))
            {
                throw new AutoAssemblerException(
                    "aobscanregion: 未找到 AOB 模式",
                    lineNumber,
                    rawText);
            }

            context.Defines.Add((symbol, $"0x{found:X}"));
            return true;
        }

        if (IsCall(text, "aobscan"))
        {
            string argumentsText = GetArguments(text, lineNumber);
            int comma = argumentsText.IndexOf(',');
            if (comma < 0)
            {
                throw new AutoAssemblerException(
                    "aobscan 语法: aobscan(name, pattern)",
                    lineNumber,
                    rawText);
            }

            string name = argumentsText[..comma].Trim();
            string pattern = argumentsText[(comma + 1)..].Trim();
            if (!TryScanTargetMemory(pattern, out ulong found))
            {
                throw new AutoAssemblerException(
                    $"aobscan: 未找到 AOB 模式 '{pattern}'",
                    lineNumber,
                    rawText);
            }

            context.Defines.Add((name, $"0x{found:X}"));
            return true;
        }

        return false;
    }

    private void ProcessAssert(string text, int lineNumber, string rawText)
    {
        string argumentsText = GetArguments(text, lineNumber);
        int comma = argumentsText.IndexOf(',');
        if (comma < 0)
        {
            throw new AutoAssemblerException(
                "assert 语法: assert(address, bytes)",
                lineNumber,
                rawText);
        }

        string addressText = SubstituteDefinesPlaceholder(argumentsText[..comma].Trim());
        if (!_resolver.TryResolve(addressText, out ulong address))
        {
            throw new AutoAssemblerException(
                $"assert: 无法解析地址 '{addressText}'",
                lineNumber,
                rawText);
        }

        byte[] expected = ParseByteSequence(argumentsText[(comma + 1)..], lineNumber, rawText);
        byte[] actual = _target.ReadBytes(address, expected.Length);
        if (actual.Length != expected.Length)
        {
            throw new AutoAssemblerException(
                $"assert: 无法读取地址 {address:X} 处的 {expected.Length} 字节",
                lineNumber,
                rawText);
        }

        for (int index = 0; index < expected.Length; index++)
        {
            if (expected[index] != actual[index])
            {
                throw new AutoAssemblerException(
                    $"assert: 地址 {address + (ulong)index:X} 处字节不匹配 " +
                    $"(期望 {expected[index]:X2}, 实际 {actual[index]:X2})",
                    lineNumber,
                    rawText);
            }
        }
    }

    private void ProcessFullAccess(string text, int lineNumber, string rawText)
    {
        string[] arguments = SplitCommaArguments(GetArguments(text, lineNumber));
        if (arguments.Length < 2)
        {
            throw new AutoAssemblerException(
                "fullaccess 语法: fullaccess(address, size)",
                lineNumber,
                rawText);
        }

        if (!_resolver.TryResolve(arguments[0], out ulong address))
        {
            throw new AutoAssemblerException(
                $"fullaccess: 无法解析地址 '{arguments[0]}'",
                lineNumber,
                rawText);
        }

        if (!TryParseSizeNumber(arguments[1], out long size) || size <= 0)
        {
            throw new AutoAssemblerException(
                $"fullaccess: 无法解析大小 '{arguments[1]}'",
                lineNumber,
                rawText);
        }

        NativeMethods.VirtualProtectEx(
            _target.Handle,
            (UIntPtr)address,
            (UIntPtr)(ulong)size,
            PageExecuteReadWrite,
            out _);
    }

    private void ProcessLoadLibrary(string text, int lineNumber, string rawText)
    {
        string path = GetArguments(text, lineNumber).Trim().Trim('"');
        if (!_target.IsOpen)
        {
            throw new AutoAssemblerException(
                "loadlibrary: 无法在目标进程中分配内存",
                lineNumber,
                rawText);
        }

        if (!_resolver.TryResolve("kernel32.LoadLibraryW", out ulong loadLibraryAddress))
        {
            throw new AutoAssemblerException(
                "loadlibrary: 无法解析 LoadLibraryW",
                lineNumber,
                rawText);
        }

        byte[] pathBytes = Encoding.Unicode.GetBytes(path + '\0');
        ulong allocation = _target.AllocateMemory(0, 0x1000, PageExecuteReadWrite);
        if (allocation == 0)
        {
            throw new AutoAssemblerException(
                "loadlibrary: 无法在目标进程中分配内存",
                lineNumber,
                rawText);
        }

        try
        {
            ulong pathAddress = allocation;
            ulong stubAddress = allocation + 0x100;
            ulong resultAddress = allocation + 0x800;
            byte[] stub = BuildLoadLibraryStub(
                _target.Is64Bit,
                pathAddress,
                loadLibraryAddress,
                resultAddress);
            if (!_target.TryWriteBytes(pathAddress, pathBytes) ||
                !_target.TryWriteBytes(stubAddress, stub))
            {
                throw new AutoAssemblerException(
                    "loadlibrary: 无法写入目标进程内存",
                    lineNumber,
                    rawText);
            }

            if (!_target.TryExecuteRemoteThreadAndWait(
                    stubAddress,
                    0,
                    0,
                    5000,
                    out uint exitCode) ||
                exitCode == 0)
            {
                throw new AutoAssemblerException(
                    "loadlibrary: 远程线程执行失败",
                    lineNumber,
                    rawText);
            }

            byte[] resultBytes = _target.ReadBytes(resultAddress, _target.Is64Bit ? 8 : 4);
            ulong moduleHandle = resultBytes.Length >= 8
                ? BitConverter.ToUInt64(resultBytes)
                : resultBytes.Length >= 4
                    ? BitConverter.ToUInt32(resultBytes)
                    : 0;
            if (moduleHandle == 0)
            {
                throw new AutoAssemblerException(
                    "loadlibrary: 无法加载模块",
                    lineNumber,
                    rawText);
            }
        }
        finally
        {
            _target.FreeMemory(allocation);
        }
    }

    private static byte[] BuildLoadLibraryStub(
        bool is64Bit,
        ulong pathAddress,
        ulong loadLibraryAddress,
        ulong resultAddress)
    {
        if (is64Bit)
        {
            return
            [
                0x48, 0x83, 0xEC, 0x28,
                0x48, 0xB9, .. BitConverter.GetBytes(pathAddress),
                0x48, 0xB8, .. BitConverter.GetBytes(loadLibraryAddress),
                0xFF, 0xD0,
                0x48, 0xB9, .. BitConverter.GetBytes(resultAddress),
                0x48, 0x89, 0x01,
                0xB8, 0x01, 0x00, 0x00, 0x00,
                0x48, 0x83, 0xC4, 0x28,
                0xC3
            ];
        }

        return
        [
            0x68, .. BitConverter.GetBytes(checked((uint)pathAddress)),
            0xB8, .. BitConverter.GetBytes(checked((uint)loadLibraryAddress)),
            0xFF, 0xD0,
            0x59,
            0xA3, .. BitConverter.GetBytes(checked((uint)resultAddress)),
            0xB8, 0x01, 0x00, 0x00, 0x00,
            0xC3
        ];
    }

    private static void ProcessInclude(
        AutoAssemblerCompileContext context,
        List<AutoAssemblerScriptLine> lines,
        ref int index,
        string text,
        int lineNumber,
        string rawText)
    {
        _ = context;
        string path = GetArguments(text, lineNumber).Trim().Trim('"');
        if (!File.Exists(path))
        {
            throw new AutoAssemblerException(
                $"include: 文件 '{path}' 不存在",
                lineNumber,
                rawText);
        }

        string[] included = File.ReadAllLines(path);
        int insertAt = index + 1;
        for (int offset = 0; offset < included.Length; offset++)
        {
            lines.Insert(
                insertAt + offset,
                new AutoAssemblerScriptLine(included[offset], -(offset + 1)));
        }
    }

    private void ExecuteContext(
        AutoAssemblerCompileContext context,
        bool enable,
        AutoAssemblerSessionState? disableState,
        AutoAssemblerSessionState newState)
    {
        // 1. Allocate all requested buffers.
        foreach (AutoAssemblerAllocationRequest request in context.Allocations)
        {
            int size = Math.Max(1, request.Size);
            ulong address = 0;
            if (request.NearAddress is { Length: > 0 } nearText &&
                _resolver.TryResolve(nearText, out ulong nearAddress))
            {
                address = AllocateNearAddress(nearAddress, size);
            }

            if (address == 0)
            {
                address = _target.AllocateMemory(0, size, PageExecuteReadWrite);
            }

            if (address == 0)
            {
                throw new AutoAssemblerException(
                    $"无法为 '{request.Name}' 分配 {request.Size} 字节内存",
                    -1);
            }

            context.AllocatedAddresses[request.Name] = address;
            newState.Allocations.Add(new AutoAssemblerAllocation
            {
                Name = request.Name,
                Address = address,
                Size = request.Size
            });
        }

        // 2. Free deallocations resolved against the previous enable state.
        if (context.DeallocateAll)
        {
            if (disableState is not null)
            {
                foreach (AutoAssemblerAllocation allocation in disableState.Allocations)
                {
                    _target.FreeMemory(allocation.Address);
                }
            }
        }
        else
        {
            foreach (string name in context.Deallocations)
            {
                AutoAssemblerAllocation? allocation = disableState?.Allocations
                    .FirstOrDefault(candidate => string.Equals(
                        candidate.Name,
                        name,
                        StringComparison.OrdinalIgnoreCase));
                if (allocation is not null)
                {
                    _target.FreeMemory(allocation.Address);
                }
            }
        }

        // 3. Unregister symbols.
        if (context.UnregisterAll)
        {
            foreach (string name in _resolver.Symbols.Keys.ToArray())
            {
                _resolver.UnregisterSymbol(name);
            }
        }
        else
        {
            foreach (string name in context.UnregisteredSymbols)
            {
                _resolver.UnregisterSymbol(name);
            }
        }

        // 4. Resolve label addresses, then assemble and write.
        ResolveLabelAddresses(context, disableState);
        AssembleAndWrite(context, disableState);

        // 5. Register symbols announced by the script.
        foreach (string name in context.RegisteredSymbols)
        {
            if (!TryResolveContextSymbol(name, context, disableState, out ulong address))
            {
                continue;
            }

            _resolver.RegisterSymbol(name, address);
            newState.Symbols[name] = address;
            newState.RegisteredSymbols.Add(name);
        }

        // 6. Remote threads.
        foreach (string name in context.CreateThreads)
        {
            if (TryResolveContextSymbol(name, context, disableState, out ulong address))
            {
                _target.CreateRemoteThread(address, 0, 0);
            }
        }

        foreach (AutoAssemblerThreadRequest thread in context.CreateThreadsAndWait)
        {
            if (TryResolveContextSymbol(thread.Name, context, disableState, out ulong address))
            {
                uint timeout = thread.TimeoutMilliseconds > 0
                    ? (uint)thread.TimeoutMilliseconds
                    : NativeMethods.Infinite;
                _ = _target.TryExecuteRemoteThreadAndWait(
                    address,
                    0,
                    0,
                    timeout,
                    out _);
            }
        }
        _ = enable;
    }

    private void ResolveLabelAddresses(
        AutoAssemblerCompileContext context,
        AutoAssemblerSessionState? disableState)
    {
        for (int pass = 0; pass < 3; pass++)
        {
            ulong currentAddress = 0;
            foreach (AutoAssemblerAssemblerLine line in context.AssemblerLines)
            {
                string substituted = SubstituteContext(line.Text, context, disableState);
                if (substituted.Contains(':'))
                {
                    string name = line.Text[..line.Text.LastIndexOf(':')].Trim();
                    string valueText = substituted[..substituted.LastIndexOf(':')].Trim();
                    DefineLabel(context, name, valueText, disableState, ref currentAddress);
                }
                else if (currentAddress != 0)
                {
                    currentAddress = currentAddress + (ulong)EstimateSize(substituted, currentAddress, context);
                }
            }
        }
    }

    /// <summary>
    /// Allocates memory close to the requested address so 32-bit relative
    /// jumps from the hook site stay encodable. Mirrors the original engine's
    /// near-allocation behavior by probing 64 KB candidates outwards.
    /// </summary>
    private ulong AllocateNearAddress(ulong preferredAddress, int size)
    {
        const ulong granularity = 0x10000;
        ulong direct = _target.AllocateMemory(preferredAddress, size, PageExecuteReadWrite);
        if (direct != 0)
        {
            return direct;
        }

        ulong baseAddress = preferredAddress & ~(granularity - 1);
        if (baseAddress != 0)
        {
            ulong aligned = _target.AllocateMemory(baseAddress, size, PageExecuteReadWrite);
            if (aligned != 0)
            {
                return aligned;
            }
        }

        const int maximumSteps = 0x400;
        for (int step = 1; step <= maximumSteps; step++)
        {
            ulong delta = (ulong)step * granularity;
            if (baseAddress > delta)
            {
                ulong candidate = baseAddress - delta;
                ulong lower = _target.AllocateMemory(candidate, size, PageExecuteReadWrite);
                if (lower != 0)
                {
                    return lower;
                }
            }

            if (baseAddress + delta > baseAddress)
            {
                ulong candidate = baseAddress + delta;
                ulong upper = _target.AllocateMemory(candidate, size, PageExecuteReadWrite);
                if (upper != 0)
                {
                    return upper;
                }
            }
        }

        return 0;
    }

    private void AssembleAndWrite(
        AutoAssemblerCompileContext context,
        AutoAssemblerSessionState? disableState)
    {
        bool is64Bit = _target.Is64Bit;
        bool anyLine = context.AssemblerLines.Count > 0;
        _log.Add($"AssembleAndWrite: {context.AssemblerLines.Count} lines, is64={is64Bit}");
        _ = anyLine;

        foreach (AutoAssemblerAllocationRequest request in context.Allocations)
        {
            ulong address = context.AllocatedAddresses.TryGetValue(request.Name, out ulong resolved)
                ? resolved
                : 0;
            _log.Add($"  alloc '{request.Name}' -> 0x{address:X} (size={request.Size})");
        }

        foreach (AutoAssemblerLabel label in context.Labels)
        {
            _log.Add($"  label '{label.Name}' -> 0x{label.Address:X} (defined={label.Defined})");
        }

        ulong currentAddress = 0;
        for (int index = 0; index < context.AssemblerLines.Count; index++)
        {
            AutoAssemblerAssemblerLine line = context.AssemblerLines[index];
            string rawText = line.Text;
            string substituted = SubstituteContext(rawText, context, disableState);
            if (substituted.Contains(':'))
            {
                ulong previous = currentAddress;
                string name = rawText[..rawText.LastIndexOf(':')].Trim();
                string valueText = substituted[..substituted.LastIndexOf(':')].Trim();
                DefineLabel(context, name, valueText, disableState, ref currentAddress);
                _log.Add(
                    $"[{index}] addr-line: raw='{rawText}' sub='{substituted}' " +
                    $"-> currentAddress: 0x{previous:X} -> 0x{currentAddress:X}");
                continue;
            }

            if (currentAddress == 0)
            {
                _log.Add(
                    $"[{index}] SKIP (currentAddress==0): raw='{rawText}' sub='{substituted}'");
                continue;
            }

            if (IsCall(substituted, "readmem"))
            {
                ProcessReadMemory(substituted, line, ref currentAddress);
                continue;
            }

            if (IsCall(substituted, "reassemble"))
            {
                ProcessReassemble(substituted, line, ref currentAddress);
                continue;
            }

            if (TryParseDataBytes(substituted, out byte[]? data))
            {
                WriteBytes(currentAddress, data, rawText);
                currentAddress += (ulong)data.Length;
                continue;
            }

            try
            {
                string source = $"use{(is64Bit ? "64" : "32")}|org 0x{currentAddress:X}|{substituted}";
                _log.Add(
                    $"[{index}] FASM: addr=0x{currentAddress:X} raw='{rawText}' " +
                    $"sub='{substituted}' src='{source}'");
                byte[] encoded = AutoAssemblerTextAssembler.Assemble(
                    substituted,
                    currentAddress,
                    is64Bit);
                _log.Add(
                    $"[{index}] FASM OK: {encoded.Length} bytes = " +
                    $"[{string.Join(' ', encoded.Select(static value => value.ToString("X2", CultureInfo.InvariantCulture)))}]");
                WriteBytes(currentAddress, encoded, rawText);
                currentAddress += (ulong)encoded.Length;
            }
            catch (Exception exception)
            {
                InstructionSnapshot? snapshot = DecodeOne(currentAddress, is64Bit);
                if (snapshot is null)
                {
                    throw new AutoAssemblerException(
                        $"汇编失败: {exception.Message}",
                        line.LineNumber,
                        rawText);
                }

                WriteBytes(currentAddress, snapshot.Bytes, rawText);
                currentAddress += (ulong)snapshot.Bytes.Length;
            }
        }
    }

    private int EstimateSize(
        string substituted,
        ulong address,
        AutoAssemblerCompileContext context)
    {
        if (IsCall(substituted, "readmem"))
        {
            string[] arguments = SplitCommaArguments(GetArguments(substituted, -1));
            if (arguments.Length >= 2 &&
                TryParseSizeNumber(arguments[1], out long size) &&
                size > 0)
            {
                return (int)size;
            }

            return 0;
        }

        if (IsCall(substituted, "reassemble"))
        {
            return 5;
        }

        if (TryParseDataBytes(substituted, out byte[]? data))
        {
            return data.Length;
        }

        try
        {
            return AutoAssemblerTextAssembler.Assemble(
                substituted,
                address,
                _target.Is64Bit).Length;
        }
        catch
        {
            return 5;
        }
    }

    private void ProcessReadMemory(
        string substituted,
        AutoAssemblerAssemblerLine line,
        ref ulong currentAddress)
    {
        string[] arguments = SplitCommaArguments(GetArguments(substituted, line.LineNumber));
        if (arguments.Length < 2 ||
            !_resolver.TryResolve(arguments[0], out ulong source) ||
            !TryParseSizeNumber(arguments[1], out long size) ||
            size == 0)
        {
            return;
        }

        byte[] bytes = _target.ReadBytes(source, (int)size);
        if (bytes.Length == 0)
        {
            return;
        }

        WriteBytes(currentAddress, bytes, line.Text);
        currentAddress += (ulong)bytes.Length;
    }

    private void ProcessReassemble(
        string substituted,
        AutoAssemblerAssemblerLine line,
        ref ulong currentAddress)
    {
        string argument = GetArguments(substituted, line.LineNumber).Trim();
        if (!_resolver.TryResolve(argument, out ulong source))
        {
            return;
        }

        InstructionSnapshot? snapshot = DecodeOne(source, _target.Is64Bit);
        if (snapshot is null)
        {
            return;
        }

        try
        {
            byte[] encoded = AutoAssemblerTextAssembler.Assemble(
                snapshot.Text,
                currentAddress,
                _target.Is64Bit);
            WriteBytes(currentAddress, encoded, line.Text);
            currentAddress += (ulong)encoded.Length;
        }
        catch
        {
            WriteBytes(currentAddress, snapshot.Bytes, line.Text);
            currentAddress += (ulong)snapshot.Bytes.Length;
        }
    }

    private void WriteBytes(ulong address, ReadOnlySpan<byte> bytes, string sourceLine)
    {
        if (bytes.Length == 0)
        {
            _log.Add($"  WriteBytes: SKIP empty data at 0x{address:X} for '{sourceLine}'");
            return;
        }

        if (!_target.TryWriteBytes(address, bytes))
        {
            _log.Add(
                $"  WriteBytes: FAILED at 0x{address:X} len={bytes.Length} for '{sourceLine}'");
            throw new AutoAssemblerException(
                $"写入目标进程失败: 地址=0x{address:X}, 大小={bytes.Length}, 指令='{sourceLine}'",
                -1,
                sourceLine);
        }

        _log.Add($"  WriteBytes: OK at 0x{address:X} len={bytes.Length}");
    }

    private void DefineLabel(
        AutoAssemblerCompileContext context,
        string rawName,
        string valueText,
        AutoAssemblerSessionState? disableState,
        ref ulong currentAddress)
    {
        if (context.AllocatedAddresses.TryGetValue(rawName, out ulong allocationAddress))
        {
            currentAddress = allocationAddress;
            MarkLabelDefined(context, rawName, currentAddress);
            return;
        }

        if (context.Labels.FirstOrDefault(
                label => string.Equals(label.Name, rawName, StringComparison.OrdinalIgnoreCase)) is { } label)
        {
            label.Address = currentAddress;
            label.Defined = true;
            return;
        }

        if (_resolver.TryResolve(valueText, out ulong resolved))
        {
            currentAddress = resolved;
        }
    }

    private static void MarkLabelDefined(
        AutoAssemblerCompileContext context,
        string name,
        ulong address)
    {
        AutoAssemblerLabel? label = context.Labels.FirstOrDefault(
            candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
        if (label is not null)
        {
            label.Address = address;
            label.Defined = true;
            return;
        }

        context.Labels.Add(new AutoAssemblerLabel
        {
            Name = name,
            Address = address,
            Defined = true
        });
    }

    private void EnsureLabel(AutoAssemblerCompileContext context, string name)
    {
        if (context.Labels.Any(
                label => string.Equals(label.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        context.Labels.Add(new AutoAssemblerLabel { Name = name, Defined = false });
    }

    private ulong ResolveContextValue(
        string valueText,
        AutoAssemblerCompileContext context,
        AutoAssemblerSessionState? disableState)
    {
        if (TryResolveContextSymbol(valueText, context, disableState, out ulong value))
        {
            return value;
        }

        return _resolver.TryResolve(valueText, out ulong resolved) ? resolved : 0;
    }

    private bool TryResolveContextSymbol(
        string name,
        AutoAssemblerCompileContext context,
        AutoAssemblerSessionState? disableState,
        out ulong address)
    {
        address = 0;
        if (context.AllocatedAddresses.TryGetValue(name, out address))
        {
            return true;
        }

        if (disableState is not null)
        {
            AutoAssemblerAllocation? allocation = disableState.Allocations.FirstOrDefault(
                candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
            if (allocation is not null)
            {
                address = allocation.Address;
                return true;
            }
        }

        AutoAssemblerLabel? label = context.Labels.FirstOrDefault(
            candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
        if (label is { Defined: true })
        {
            address = label.Address;
            return true;
        }

        if (disableState is not null && disableState.Symbols.TryGetValue(name, out address))
        {
            return true;
        }

        return _resolver.TryResolve(name, out address);
    }

    private string SubstituteContext(
        string text,
        AutoAssemblerCompileContext context,
        AutoAssemblerSessionState? disableState)
    {
        string result = text;
        foreach ((string name, ulong address) in context.AllocatedAddresses)
        {
            if (address != 0)
            {
                result = ReplaceToken(result, name, $"0x{address:X}");
            }
        }

        if (disableState is not null)
        {
            foreach (AutoAssemblerAllocation allocation in disableState.Allocations)
            {
                result = ReplaceToken(result, allocation.Name, $"0x{allocation.Address:X}");
            }
        }

        foreach (AutoAssemblerLabel label in context.Labels)
        {
            if (label.Defined)
            {
                result = ReplaceToken(result, label.Name, $"0x{label.Address:X}");
            }
        }

        foreach ((string name, string value) in context.Defines)
        {
            result = ReplaceToken(result, name, value);
        }

        if (disableState is not null)
        {
            foreach ((string name, ulong address) in disableState.Symbols)
            {
                result = ReplaceToken(result, name, $"0x{address:X}");
            }
        }

        return result;
    }

    private string SubstituteDefines(AutoAssemblerCompileContext context, string text)
    {
        string result = text;
        foreach ((string name, string value) in context.Defines)
        {
            result = ReplaceToken(result, name, value);
        }

        return result;
    }

    private static string SubstituteDefinesPlaceholder(string text) => text;

    private InstructionSnapshot? DecodeOne(ulong address, bool is64Bit)
    {
        byte[] bytes = _target.ReadBytes(address, 15);
        if (bytes.Length == 0)
        {
            return null;
        }

        ByteArrayCodeReader reader = new(bytes);
        Iced.Intel.Decoder decoder = Iced.Intel.Decoder.Create(is64Bit ? 64 : 32, reader);
        decoder.IP = address;
        Instruction instruction = decoder.Decode();
        if (instruction.IsInvalid || instruction.Length == 0 || instruction.Length > bytes.Length)
        {
            return null;
        }

        MasmFormatter formatter = new();
        StringOutput output = new();
        formatter.Format(instruction, output);
        return new InstructionSnapshot
        {
            Address = address,
            Length = instruction.Length,
            Bytes = bytes[..instruction.Length],
            Text = output.ToString()
        };
    }

    private ModuleDescriptor? FindModule(string name) =>
        _resolver.GetModules().FirstOrDefault(module =>
            string.Equals(module.Name, name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(module.FilePath, name, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrEmpty(module.FilePath) &&
             module.FilePath.EndsWith(name, StringComparison.OrdinalIgnoreCase)));

    private bool TryScanTargetMemory(string pattern, out ulong address)
    {
        address = 0;
        foreach (MemoryRegionInfo region in _regions.Enumerate(_target))
        {
            if (region.State != 0x1000 || !region.IsReadable)
            {
                continue;
            }

            if (TryScanRange(region.BaseAddress, region.BaseAddress + region.Size, pattern, out address))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryScanRange(ulong start, ulong end, string patternText, out ulong address)
    {
        address = 0;
        if (!BytePattern.TryParse(patternText, out BytePattern? pattern, out _) ||
            pattern is null ||
            end <= start)
        {
            return false;
        }

        const int chunkSize = 1 << 16;
        const int pageSize = 1 << 12;
        int overlap = Math.Max(0, pattern.Length - 1);
        ulong position = start;
        byte[] carry = [];
        while (position < end)
        {
            int requested = (int)Math.Min((ulong)chunkSize, end - position);
            byte[] chunk = _target.ReadBytes(position, requested);
            while (chunk.Length == 0 && requested > pageSize)
            {
                requested = pageSize;
                chunk = _target.ReadBytes(position, requested);
            }

            if (chunk.Length == 0)
            {
                position += (ulong)Math.Max(pageSize, requested);
                carry = [];
                continue;
            }

            byte[] combined = new byte[carry.Length + chunk.Length];
            carry.CopyTo(combined, 0);
            chunk.CopyTo(combined, carry.Length);
            for (int index = 0; index <= combined.Length - pattern.Length; index++)
            {
                if (pattern.Matches(combined.AsSpan(index, pattern.Length)))
                {
                    address = position - (ulong)carry.Length + (ulong)index;
                    return true;
                }
            }

            carry = combined.Length >= overlap
                ? combined[^overlap..]
                : combined;
            position += (ulong)chunk.Length;
        }

        return false;
    }

    private static bool TryParseDataBytes(string text, out byte[] bytes)
    {
        bytes = [];
        string trimmed = text.TrimStart();
        int elementSize;
        if (trimmed.StartsWith("db ", StringComparison.OrdinalIgnoreCase))
        {
            elementSize = 1;
        }
        else if (trimmed.StartsWith("dw ", StringComparison.OrdinalIgnoreCase))
        {
            elementSize = 2;
        }
        else if (trimmed.StartsWith("dd ", StringComparison.OrdinalIgnoreCase))
        {
            elementSize = 4;
        }
        else if (trimmed.StartsWith("dq ", StringComparison.OrdinalIgnoreCase))
        {
            elementSize = 8;
        }
        else
        {
            return false;
        }

        string body = text[3..].Trim();
        if (elementSize == 1 && body.Length >= 2 && body[0] == '\'' && body[^1] == '\'')
        {
            bytes = Encoding.ASCII.GetBytes(body[1..^1]);
            return true;
        }

        List<byte> result = [];
        foreach (string token in body.Split(
                     [' ', ',', '\t'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.Length >= 2 && token[0] == '\'' && token[^1] == '\'')
            {
                result.AddRange(Encoding.ASCII.GetBytes(token[1..^1]));
                continue;
            }

            if (!TryParseFlexibleNumber(token, out ulong value))
            {
                return false;
            }

            byte[] encoded = BitConverter.GetBytes(value);
            for (int index = 0; index < elementSize; index++)
            {
                result.Add(index < encoded.Length ? encoded[index] : (byte)0);
            }
        }

        bytes = [.. result];
        return true;
    }

    private static byte[] ParseByteSequence(string text, int lineNumber, string rawText)
    {
        List<byte> bytes = [];
        foreach (string token in text.Split(
                     [' ', ',', '\t'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token is "?" or "??" or "*")
            {
                bytes.Add(0);
                continue;
            }

            if (!byte.TryParse(
                    token,
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out byte value))
            {
                throw new AutoAssemblerException($"无效的字节值 '{token}'", lineNumber, rawText);
            }

            bytes.Add(value);
        }

        return [.. bytes];
    }

    private static bool TryParseHexNumber(string text, out ulong value)
    {
        value = 0;
        string candidate = text.Trim();
        if (candidate.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate[2..];
        }
        else if (candidate.StartsWith('$'))
        {
            candidate = candidate[1..];
        }

        return candidate.Length > 0 &&
               ulong.TryParse(
                   candidate,
                   NumberStyles.HexNumber,
                   CultureInfo.InvariantCulture,
                   out value);
    }

    private static bool TryParseFlexibleNumber(string text, out ulong value)
    {
        if (TryParseHexNumber(text, out value))
        {
            return true;
        }

        return ulong.TryParse(
            text,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out value);
    }

    /// <summary>
    /// Parses a size literal the way the original engine does: explicit
    /// `0x`/`$` prefixes are hexadecimal, otherwise decimal wins over hex.
    /// </summary>
    private static bool TryParseSizeNumber(string text, out long value)
    {
        value = 0;
        string candidate = text.Trim();
        if (candidate.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith('$'))
        {
            if (!TryParseHexNumber(candidate, out ulong hexadecimal))
            {
                return false;
            }

            value = unchecked((long)hexadecimal);
            return true;
        }

        if (long.TryParse(
                candidate,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out value))
        {
            return true;
        }

        if (TryParseHexNumber(candidate, out ulong fallbackHex))
        {
            value = unchecked((long)fallbackHex);
            return true;
        }

        return false;
    }

    private static string GetArguments(string text, int lineNumber)
    {
        int open = text.IndexOf('(');
        int close = text.LastIndexOf(')');
        if (open < 0 || close <= open)
        {
            throw new AutoAssemblerException($"语法错误: {text}", lineNumber, text);
        }

        return text[(open + 1)..close];
    }

    private static (string Name, string Value) SplitPair(string arguments, string command)
    {
        int comma = arguments.IndexOf(',');
        if (comma < 0)
        {
            throw new AutoAssemblerException($"{command} 需要两个参数");
        }

        return (arguments[..comma].Trim(), arguments[(comma + 1)..].Trim());
    }

    private static string[] SplitArguments(string arguments) =>
        arguments
            .Split(
                [',', ' ', '\t'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();

    private static string TrimQuotes(string value) => value.Trim().Trim('"');

    /// <summary>
    /// Splits comma-separated command arguments while preserving spaces inside
    /// each argument (for example an AOB pattern). A positive
    /// <paramref name="maximumParts"/> keeps trailing commas inside the last
    /// argument, mirroring the original parser's limited splits.
    /// </summary>
    private static string[] SplitCommaArguments(string arguments, int maximumParts = 0)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return [];
        }

        string[] parts = maximumParts > 0
            ? arguments.Split(',', maximumParts, StringSplitOptions.None)
            : arguments.Split(',', StringSplitOptions.None);
        for (int index = 0; index < parts.Length; index++)
        {
            parts[index] = parts[index].Trim();
        }

        return parts;
    }

    private static bool IsCall(string text, string command) =>
        text.StartsWith(command + "(", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetAddressLineName(string text, out string name)
    {
        name = string.Empty;
        int colon = text.LastIndexOf(':');
        if (colon <= 0)
        {
            return false;
        }

        string candidate = text[..colon].Trim();
        if (candidate.Length == 0 ||
            !(char.IsLetter(candidate[0]) || candidate[0] == '_') ||
            candidate.Contains('+') ||
            candidate.Contains('.') ||
            candidate.Contains(' '))
        {
            return false;
        }

        name = candidate;
        return true;
    }

    private static string ReplaceToken(string text, string token, string replacement)
    {
        if (token.Length == 0 || !text.Contains(token, StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        StringBuilder builder = new(text.Length + 16);
        int position = 0;
        while (position < text.Length)
        {
            int found = text.IndexOf(token, position, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
            {
                builder.Append(text, position, text.Length - position);
                break;
            }

            builder.Append(text, position, found - position);
            builder.Append(replacement);
            position = found + token.Length;
        }

        return builder.ToString();
    }

    private sealed class AutoAssemblerCompileContext
    {
        public HashSet<string> LocalLabels { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        public List<(string Name, string Value)> Defines { get; } = [];

        public List<AutoAssemblerAllocationRequest> Allocations { get; } = [];

        public List<string> Deallocations { get; } = [];

        public bool DeallocateAll { get; set; }

        public List<AutoAssemblerLabel> Labels { get; } = [];

        public List<string> RegisteredSymbols { get; } = [];

        public List<string> UnregisteredSymbols { get; } = [];

        public bool UnregisterAll { get; set; }

        public List<string> CreateThreads { get; } = [];

        public List<AutoAssemblerThreadRequest> CreateThreadsAndWait { get; } = [];

        public List<AutoAssemblerAssemblerLine> AssemblerLines { get; } = [];

        public Dictionary<string, ulong> AllocatedAddresses { get; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class AutoAssemblerAllocationRequest
    {
        public string Name { get; init; } = string.Empty;

        public int Size { get; init; }

        public string? NearAddress { get; init; }
    }

    private sealed class AutoAssemblerLabel
    {
        public string Name { get; init; } = string.Empty;

        public ulong Address { get; set; }

        public bool Defined { get; set; }
    }

    private sealed class AutoAssemblerThreadRequest
    {
        public string Name { get; init; } = string.Empty;

        public int LineIndex { get; init; }

        public int TimeoutMilliseconds { get; init; }
    }

    private readonly record struct AutoAssemblerAssemblerLine(string Text, int LineNumber);
}
