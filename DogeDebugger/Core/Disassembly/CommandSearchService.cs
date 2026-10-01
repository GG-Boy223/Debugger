using DogeDebugger.Core.Memory;
using DogeDebugger.Core.Modules;
using DogeDebugger.Core.Native;
using DogeDebugger.Core.Process;
using DogeDebugger.Core.Scripting.AutoAssembler;
using Iced.Intel;

namespace DogeDebugger.Core.Disassembly;

public sealed class CommandSearchService
{
    private const ulong MaximumModuleScanBytes = 4UL * 1024 * 1024;
    private const int ChunkSize = 4 * 1024 * 1024;

    private readonly MemoryRegionCatalog _memoryRegions =
        new(new ModuleCatalog());

    public CommandSearchScanResult Search(
        ITargetProcess target,
        CommandSearchRequest request,
        IProgress<(double Progress, string Message)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(request);
        if (!target.IsOpen)
        {
            throw new InvalidOperationException("目标进程未打开。");
        }

        if (request.ScanModules.Count == 0)
        {
            throw new InvalidOperationException("请至少选择一个模块。");
        }

        string assemblyText = request.AssemblyText.Trim();
        if (assemblyText.Length == 0 || assemblyText.Contains('\r') ||
            assemblyText.Contains('\n'))
        {
            throw new InvalidOperationException(
                "请输入单条汇编指令；当前版本不支持整块或多行指令搜索。");
        }

        byte[] patternBytes = AutoAssemblerTextAssembler.Assemble(
            assemblyText,
            request.AssemblyAddress,
            request.Bitness == 64);
        Instruction pattern = DecodeOne(
            patternBytes,
            request.AssemblyAddress,
            request.Bitness);
        CommandSearchMatchMode matchMode = HasVariableOperand(pattern)
            ? CommandSearchMatchMode.Semantic
            : CommandSearchMatchMode.ExactBytes;

        List<CommandSearchResultItem> results = [];
        int maximumResults = Math.Max(1, request.MaxResults);
        bool truncated = false;
        ulong scannedBytes = 0;
        int scannedRegionCount = 0;
        System.Diagnostics.Stopwatch stopwatch =
            System.Diagnostics.Stopwatch.StartNew();

        for (int moduleIndex = 0;
             moduleIndex < request.ScanModules.Count && !truncated;
             moduleIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var module = request.ScanModules[moduleIndex];
            ulong moduleSize = Math.Min(module.Size, MaximumModuleScanBytes);
            foreach (ExecutableScanRegion scanRegion in EnumerateExecutableRegions(
                         target,
                         module,
                         moduleSize,
                         cancellationToken))
            {
                scannedRegionCount++;
                ulong address = scanRegion.BaseAddress;
                while (address < scanRegion.EndAddress && !truncated)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int requestedSize = (int)Math.Min(
                        (ulong)ChunkSize,
                        scanRegion.EndAddress - address);
                    byte[] bytes = new byte[requestedSize];
                    if (!target.TryReadBytes(address, bytes) ||
                        bytes.All(static value => value == 0))
                    {
                        address += (uint)requestedSize;
                        scannedBytes += (uint)requestedSize;
                        continue;
                    }

                    ScanChunk(
                        bytes,
                        address,
                        module.Name,
                        module.BaseAddress,
                        request,
                        pattern,
                        patternBytes,
                        matchMode,
                        results,
                        maximumResults,
                        ref truncated,
                        ref scannedBytes,
                        cancellationToken);
                    address += (uint)requestedSize;

                    if (progress is not null && moduleSize > 0)
                    {
                        double value =
                            (double)(address - module.BaseAddress) /
                            moduleSize;
                        progress.Report((
                            Math.Clamp(value, 0, 1),
                            $"正在扫描 {module.Name}... {value:P0}"));
                    }
                }
            }
        }

        stopwatch.Stop();
        progress?.Report((
            1,
            truncated ? "搜索完成，结果已截断。" : "搜索完成。"));
        return new CommandSearchScanResult
        {
            Results = results,
            PatternBytes = patternBytes,
            MatchMode = matchMode,
            ScannedBytes = scannedBytes,
            ScannedRegionCount = scannedRegionCount,
            Truncated = truncated,
            Elapsed = stopwatch.Elapsed
        };
    }

    public CommandSearchScanResult SearchReferences(
        ITargetProcess target,
        InstructionReferenceSearchRequest request,
        IProgress<(double Progress, string Message)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(request);
        if (!target.IsOpen)
        {
            throw new InvalidOperationException("目标进程未打开。");
        }

        if (request.ScanModules.Count == 0)
        {
            throw new InvalidOperationException("请至少选择一个模块。");
        }

        List<CommandSearchResultItem> results = [];
        int maximumResults = Math.Max(1, request.MaxResults);
        bool truncated = false;
        ulong scannedBytes = 0;
        int scannedRegionCount = 0;
        System.Diagnostics.Stopwatch stopwatch =
            System.Diagnostics.Stopwatch.StartNew();

        for (int moduleIndex = 0;
             moduleIndex < request.ScanModules.Count && !truncated;
             moduleIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var module = request.ScanModules[moduleIndex];
            ulong moduleSize = Math.Min(module.Size, MaximumModuleScanBytes);
            foreach (ExecutableScanRegion scanRegion in EnumerateExecutableRegions(
                         target,
                         module,
                         moduleSize,
                         cancellationToken))
            {
                scannedRegionCount++;
                ulong address = scanRegion.BaseAddress;
                while (address < scanRegion.EndAddress && !truncated)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int requestedSize = (int)Math.Min(
                        (ulong)ChunkSize,
                        scanRegion.EndAddress - address);
                    byte[] bytes = new byte[requestedSize];
                    if (!target.TryReadBytes(address, bytes) ||
                        bytes.All(static value => value == 0))
                    {
                        address += (uint)requestedSize;
                        scannedBytes += (uint)requestedSize;
                        continue;
                    }

                    ScanReferenceChunk(
                        bytes,
                        address,
                        module.Name,
                        module.BaseAddress,
                        request,
                        results,
                        maximumResults,
                        ref truncated,
                        ref scannedBytes,
                        cancellationToken);
                    address += (uint)requestedSize;

                    if (progress is not null && moduleSize > 0)
                    {
                        double value =
                            (double)(address - module.BaseAddress) /
                            moduleSize;
                        progress.Report((
                            Math.Clamp(value, 0, 1),
                            $"正在扫描 {module.Name}... {value:P0}"));
                    }
                }
            }
        }

        stopwatch.Stop();
        progress?.Report((
            1,
            truncated ? "搜索完成，结果已截断。" : "搜索完成。"));
        return new CommandSearchScanResult
        {
            Results = results,
            PatternBytes = [],
            MatchMode = CommandSearchMatchMode.Semantic,
            ScannedBytes = scannedBytes,
            ScannedRegionCount = scannedRegionCount,
            Truncated = truncated,
            Elapsed = stopwatch.Elapsed
        };
    }

    private IEnumerable<ExecutableScanRegion> EnumerateExecutableRegions(
        ITargetProcess target,
        ModuleDescriptor module,
        ulong moduleSize,
        CancellationToken cancellationToken)
    {
        ulong moduleEnd = module.BaseAddress + moduleSize;
        if (moduleEnd < module.BaseAddress)
        {
            moduleEnd = ulong.MaxValue;
        }

        foreach (MemoryRegionInfo region in _memoryRegions.Enumerate(target))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (region.State != NativeMethods.MemCommit ||
                !region.IsExecutable ||
                region.Size == 0)
            {
                continue;
            }

            ulong regionEnd = region.BaseAddress + region.Size;
            if (regionEnd < region.BaseAddress)
            {
                regionEnd = ulong.MaxValue;
            }

            ulong start = Math.Max(region.BaseAddress, module.BaseAddress);
            ulong end = Math.Min(regionEnd, moduleEnd);
            if (end > start)
            {
                yield return new ExecutableScanRegion(start, end);
            }
        }
    }

    private static void ScanChunk(
        byte[] bytes,
        ulong address,
        string moduleName,
        ulong moduleBase,
        CommandSearchRequest request,
        Instruction pattern,
        byte[] patternBytes,
        CommandSearchMatchMode matchMode,
        List<CommandSearchResultItem> results,
        int maximumResults,
        ref bool truncated,
        ref ulong scannedBytes,
        CancellationToken cancellationToken)
    {
        ByteArrayCodeReader reader = new(bytes);
        Decoder decoder = Decoder.Create(
            request.Bitness == 64 ? 64 : 32,
            reader);
        decoder.IP = address;
        Formatter formatter = CreateFormatter(request.SyntaxFormat);
        ulong end = address + (ulong)bytes.Length;

        while (decoder.IP < end && !truncated)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Instruction instruction = decoder.Decode();
            if (instruction.IsInvalid || instruction.Length <= 0)
            {
                break;
            }

            int offset = checked((int)(instruction.IP - address));
            if (offset < 0 || offset + instruction.Length > bytes.Length)
            {
                break;
            }

            ReadOnlySpan<byte> candidateBytes =
                bytes.AsSpan(offset, instruction.Length);
            bool match = matchMode == CommandSearchMatchMode.ExactBytes
                ? candidateBytes.SequenceEqual(patternBytes)
                : SemanticEquals(pattern, instruction);
            if (match)
            {
                StringOutput output = new();
                formatter.Format(instruction, output);
                results.Add(new CommandSearchResultItem
                {
                    Address = instruction.IP,
                    ModuleName = moduleName,
                    ModuleBase = moduleBase,
                    ModuleOffset = instruction.IP - moduleBase,
                    Bytes = candidateBytes.ToArray(),
                    Disassembly = output.ToString()
                });
                if (results.Count >= maximumResults)
                {
                    truncated = true;
                }
            }

            scannedBytes += (uint)instruction.Length;
            decoder.IP = instruction.IP + (uint)instruction.Length;
        }
    }

    private static void ScanReferenceChunk(
        byte[] bytes,
        ulong address,
        string moduleName,
        ulong moduleBase,
        InstructionReferenceSearchRequest request,
        List<CommandSearchResultItem> results,
        int maximumResults,
        ref bool truncated,
        ref ulong scannedBytes,
        CancellationToken cancellationToken)
    {
        ByteArrayCodeReader reader = new(bytes);
        Decoder decoder = Decoder.Create(
            request.Bitness == 64 ? 64 : 32,
            reader);
        decoder.IP = address;
        Formatter formatter = CreateFormatter(request.SyntaxFormat);
        ulong end = address + (ulong)bytes.Length;

        while (decoder.IP < end && !truncated)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Instruction instruction = decoder.Decode();
            if (instruction.IsInvalid || instruction.Length <= 0)
            {
                break;
            }

            int offset = checked((int)(instruction.IP - address));
            if (offset < 0 || offset + instruction.Length > bytes.Length)
            {
                break;
            }

            ConstantOffsets constantOffsets =
                decoder.GetConstantOffsets(instruction);
            if (MatchesReference(
                    in instruction,
                    in constantOffsets,
                    request))
            {
                StringOutput output = new();
                formatter.Format(instruction, output);
                results.Add(new CommandSearchResultItem
                {
                    Address = instruction.IP,
                    ModuleName = moduleName,
                    ModuleBase = moduleBase,
                    ModuleOffset = instruction.IP - moduleBase,
                    Bytes = bytes.AsSpan(offset, instruction.Length).ToArray(),
                    Disassembly = output.ToString()
                });
                if (results.Count >= maximumResults)
                {
                    truncated = true;
                }
            }

            scannedBytes += (uint)instruction.Length;
            decoder.IP = instruction.IP + (uint)instruction.Length;
        }
    }

    private static bool MatchesReference(
        in Instruction instruction,
        in ConstantOffsets constantOffsets,
        InstructionReferenceSearchRequest request)
    {
        for (int operandIndex = 0;
             operandIndex < instruction.OpCount;
             operandIndex++)
        {
            OpKind kind = instruction.GetOpKind(operandIndex);
            if (IsNearBranch(kind))
            {
                if (request.Kind == InstructionReferenceKind.Address &&
                    instruction.NearBranchTarget == request.Value)
                {
                    return true;
                }

                continue;
            }

            if (kind == OpKind.Memory)
            {
                if (MatchesMemoryReference(
                        in instruction,
                        in constantOffsets,
                        request))
                {
                    return true;
                }

                continue;
            }

            if (IsImmediate(kind) &&
                instruction.GetImmediate(operandIndex) == request.Value)
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesMemoryReference(
        in Instruction instruction,
        in ConstantOffsets constantOffsets,
        InstructionReferenceSearchRequest request)
    {
        if (!constantOffsets.HasDisplacement)
        {
            return false;
        }

        if (instruction.MemorySegment is Register.FS or Register.GS)
        {
            return request.Kind == InstructionReferenceKind.Constant &&
                   instruction.MemoryDisplacement64 == request.Value;
        }

        if (instruction.MemoryBase is Register.EIP or Register.RIP)
        {
            return request.Kind == InstructionReferenceKind.Address &&
                   instruction.IPRelativeMemoryAddress == request.Value;
        }

        if (instruction.MemoryBase == Register.None &&
            instruction.MemoryIndex == Register.None)
        {
            return request.Kind == InstructionReferenceKind.Address &&
                   instruction.MemoryDisplacement64 == request.Value;
        }

        return request.Kind == InstructionReferenceKind.Constant &&
               instruction.MemoryDisplacement64 == request.Value;
    }

    private static bool IsNearBranch(OpKind kind) =>
        kind is OpKind.NearBranch16 or
            OpKind.NearBranch32 or
            OpKind.NearBranch64;

    private static bool IsImmediate(OpKind kind) =>
        kind is >= OpKind.Immediate8 and <= OpKind.Immediate32to64;

    private static Instruction DecodeOne(
        byte[] bytes,
        ulong address,
        int bitness)
    {
        if (bytes.Length == 0)
        {
            throw new InvalidOperationException("汇编结果为空。");
        }

        Decoder decoder = Decoder.Create(
            bitness == 64 ? 64 : 32,
            new ByteArrayCodeReader(bytes));
        decoder.IP = address;
        Instruction instruction = decoder.Decode();
        if (instruction.IsInvalid || instruction.Length <= 0)
        {
            throw new InvalidOperationException("汇编结果不是有效指令。");
        }

        if (instruction.Length != bytes.Length)
        {
            throw new InvalidOperationException(
                "请输入单条汇编指令；当前输入会汇编成多条指令。");
        }

        return instruction;
    }

    private static bool HasVariableOperand(in Instruction instruction)
    {
        for (int index = 0; index < instruction.OpCount; index++)
        {
            OpKind kind = instruction.GetOpKind(index);
            if (kind is OpKind.Register or
                OpKind.Memory or
                OpKind.MemorySegRSI or
                OpKind.MemorySegESI or
                OpKind.MemoryESRDI or
                OpKind.MemoryESEDI)
            {
                return true;
            }
        }

        return false;
    }

    private static bool SemanticEquals(
        in Instruction expected,
        in Instruction candidate)
    {
        if (expected.Code != candidate.Code ||
            expected.Mnemonic != candidate.Mnemonic ||
            expected.OpCount != candidate.OpCount)
        {
            return false;
        }

        for (int index = 0; index < expected.OpCount; index++)
        {
            OpKind expectedKind = expected.GetOpKind(index);
            OpKind candidateKind = candidate.GetOpKind(index);
            if (expectedKind != candidateKind)
            {
                return false;
            }

            switch (expectedKind)
            {
                case OpKind.Register:
                    if (expected.GetOpRegister(index) !=
                        candidate.GetOpRegister(index))
                    {
                        return false;
                    }

                    break;

                case OpKind.Memory:
                case OpKind.MemorySegRSI:
                case OpKind.MemorySegESI:
                case OpKind.MemoryESRDI:
                case OpKind.MemoryESEDI:
                    if (expected.MemoryBase != candidate.MemoryBase ||
                        expected.MemoryIndex != candidate.MemoryIndex ||
                        expected.MemoryIndexScale !=
                            candidate.MemoryIndexScale ||
                        expected.MemoryDisplacement64 !=
                            candidate.MemoryDisplacement64)
                    {
                        return false;
                    }

                    break;

                default:
                    if (!ImmediateEquals(expected, candidate, index))
                    {
                        return false;
                    }

                    break;
            }
        }

        return true;
    }

    private static bool ImmediateEquals(
        in Instruction expected,
        in Instruction candidate,
        int operandIndex)
    {
        OpKind kind = expected.GetOpKind(operandIndex);
        return kind switch
        {
            OpKind.Immediate8 =>
                expected.Immediate8 == candidate.Immediate8,
            OpKind.Immediate8_2nd =>
                expected.Immediate8_2nd == candidate.Immediate8_2nd,
            OpKind.Immediate16 =>
                expected.Immediate16 == candidate.Immediate16,
            OpKind.Immediate32 =>
                expected.Immediate32 == candidate.Immediate32,
            OpKind.Immediate64 =>
                expected.Immediate64 == candidate.Immediate64,
            OpKind.Immediate8to16 =>
                expected.Immediate8to16 == candidate.Immediate8to16,
            OpKind.Immediate8to32 =>
                expected.Immediate8to32 == candidate.Immediate8to32,
            OpKind.Immediate8to64 =>
                expected.Immediate8to64 == candidate.Immediate8to64,
            OpKind.Immediate32to64 =>
                expected.Immediate32to64 == candidate.Immediate32to64,
            _ => true
        };
    }

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

    private readonly record struct ExecutableScanRegion(
        ulong BaseAddress,
        ulong EndAddress);
}
