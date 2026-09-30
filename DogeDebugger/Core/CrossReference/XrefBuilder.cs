using System.Diagnostics;
using DogeDebugger.Core.Disassembly;
using DogeDebugger.Core.Memory;
using DogeDebugger.Core.Modules;
using DogeDebugger.Core.Native;
using DogeDebugger.Core.Process;
using Iced.Intel;

namespace DogeDebugger.Core.CrossReference;

public sealed class XrefBuilder
{
    private const int ProgressIntervalBytes = 64 * 1024;
    private const int MaximumLegacyInstructions = 4_000_000;

    private readonly DisassemblerService _disassembler = new();
    private readonly PeModuleAnalyzer _peAnalyzer = new();

    public XrefDatabase Build(
        ITargetProcess process,
        IReadOnlyList<ModuleDescriptor> modules,
        IReadOnlyList<MemoryRegionInfo> regions,
        CancellationToken cancellationToken = default)
    {
        List<XrefEntry> entries = [];
        int instructionCount = 0;
        Span<byte> bytes = stackalloc byte[16];

        foreach (MemoryRegionInfo region in regions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!region.IsExecutable || !region.IsReadable ||
                region.Protect == NativeMethods.PageNoAccess)
            {
                continue;
            }

            ulong address = region.BaseAddress;
            ulong end = region.BaseAddress + region.Size;
            while (address < end && instructionCount < MaximumLegacyInstructions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!process.TryReadBytes(address, bytes))
                {
                    break;
                }

                InstructionSnapshot? instruction = _disassembler.DisassembleOne(
                    bytes,
                    address,
                    process.Is64Bit);
                if (instruction is null || instruction.Length <= 0)
                {
                    address++;
                    continue;
                }

                ModuleDescriptor? module = ModuleCatalog.FindByAddress(modules, address);
                foreach (ulong reference in instruction.ReferencedAddresses)
                {
                    if (reference == 0 || reference == instruction.Address)
                    {
                        continue;
                    }

                    entries.Add(new XrefEntry
                    {
                        FromAddress = instruction.Address,
                        ToAddress = reference,
                        Kind = Classify(instruction),
                        ModuleName = module?.Name ?? string.Empty,
                        InstructionText = instruction.Text
                    });
                }

                instructionCount++;
                address += checked((uint)instruction.Length);
            }
        }

        return XrefDatabase.FromEntries(entries);
    }

    public XrefDatabase BuildModule(
        ITargetProcess process,
        ModuleDescriptor module,
        string? pdbPath = null,
        IProgress<(double Progress, string Message)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(module);
        if (!process.IsOpen)
        {
            throw new InvalidOperationException("目标进程未打开");
        }

        if (module.Size < 1024)
        {
            throw new InvalidOperationException("模块尺寸过小，疑似无效");
        }

        if (string.IsNullOrWhiteSpace(module.FilePath))
        {
            throw new InvalidOperationException("模块文件路径不可用");
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        progress?.Report((0, $"正在读取模块内存 ({module.Size / 1024:N0} KB)..."));
        PeModuleMetadata metadata = _peAnalyzer.AnalyzeFile(module.FilePath);
        cancellationToken.ThrowIfCancellationRequested();

        progress?.Report((0.05, "正在枚举函数..."));
        List<FunctionEntry> functions = EnumerateFunctions(metadata, pdbPath, cancellationToken);
        FunctionEntry[] mergedFunctions = MergeFunctions(functions, metadata.SizeOfImage);
        progress?.Report((
            0.12,
            $"函数来源: .pdata={metadata.RuntimeFunctions.Count:N0}, " +
            $"导出={metadata.Exports.Count:N0}, PDB={Math.Max(0, functions.Count - metadata.RuntimeFunctions.Count - metadata.Exports.Count):N0}"));

        progress?.Report((0.15, "正在反汇编代码段..."));
        List<XrefRecord> records = ScanExecutableSections(
            process,
            module,
            metadata,
            mergedFunctions,
            progress,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        progress?.Report((0.95, "正在构建交叉引用索引..."));
        XrefDatabase database = BuildDatabase(
            module,
            metadata,
            mergedFunctions,
            records,
            stopwatch.Elapsed);
        progress?.Report((
            1,
            $"完成 - {database.TotalFunctions:N0} 个函数, " +
            $"{database.TotalXrefs:N0} 条交叉引用"));
        return database;
    }

    private static List<FunctionEntry> EnumerateFunctions(
        PeModuleMetadata metadata,
        string? pdbPath,
        CancellationToken cancellationToken)
    {
        List<FunctionEntry> functions = new(
            metadata.RuntimeFunctions.Count + metadata.Exports.Count + 1);
        foreach (PeRuntimeFunction function in metadata.RuntimeFunctions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (function.BeginRva != 0)
            {
                functions.Add(new FunctionEntry(
                    function.BeginRva,
                    function.EndRva,
                    null,
                    FunctionSource.Pdata));
            }
        }

        foreach (PeExport export in metadata.Exports)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (export.ForwarderName is null && export.FunctionRva != 0)
            {
                functions.Add(new FunctionEntry(
                    export.FunctionRva,
                    0,
                    export.Name,
                    FunctionSource.Export));
            }
        }

        if (metadata.EntryPointRva != 0)
        {
            functions.Add(new FunctionEntry(
                metadata.EntryPointRva,
                0,
                "entry_point",
                FunctionSource.Export));
        }

        // PDB parsing is intentionally isolated here. Portable and legacy PDB
        // readers can be added without changing the scanner or database layout.
        _ = pdbPath;
        return functions;
    }

    private static FunctionEntry[] MergeFunctions(
        IEnumerable<FunctionEntry> source,
        uint imageSize)
    {
        List<FunctionEntry> functions = source
            .Where(function =>
                function.StartRva != 0 &&
                function.StartRva < imageSize &&
                (function.EndRva == 0 || function.EndRva <= imageSize))
            .OrderBy(static function => function.StartRva)
            .ToList();
        if (functions.Count == 0)
        {
            return [];
        }

        List<FunctionEntry> merged = new(functions.Count);
        FunctionEntry current = functions[0];
        for (int index = 1; index < functions.Count; index++)
        {
            FunctionEntry candidate = functions[index];
            if (candidate.StartRva == current.StartRva)
            {
                string? name = string.IsNullOrWhiteSpace(current.Name)
                    ? candidate.Name
                    : current.Name;
                FunctionSource functionSource = string.IsNullOrWhiteSpace(current.Name)
                    ? candidate.Source
                    : current.Source;
                current = new FunctionEntry(
                    current.StartRva,
                    Math.Max(current.EndRva, candidate.EndRva),
                    name,
                    functionSource);
                continue;
            }

            merged.Add(current);
            current = candidate;
        }

        merged.Add(current);
        for (int index = 0; index < merged.Count; index++)
        {
            FunctionEntry function = merged[index];
            if (function.EndRva != 0)
            {
                continue;
            }

            uint nextStart = index + 1 < merged.Count
                ? merged[index + 1].StartRva
                : checked(function.StartRva + 1);
            if (nextStart - function.StartRva > 1024 * 1024)
            {
                nextStart = checked(function.StartRva + 1);
            }

            merged[index] = function with { EndRva = nextStart };
        }

        return merged.ToArray();
    }

    private static List<XrefRecord> ScanExecutableSections(
        ITargetProcess process,
        ModuleDescriptor module,
        PeModuleMetadata metadata,
        FunctionEntry[] functions,
        IProgress<(double Progress, string Message)>? progress,
        CancellationToken cancellationToken)
    {
        ulong totalBytes = 0;
        foreach (PeSection section in metadata.Sections)
        {
            if (!section.IsExecutable)
            {
                continue;
            }

            uint byteCount = section.RawSize > 0 ? section.RawSize : section.VirtualSize;
            totalBytes += byteCount;
        }

        List<XrefRecord> records = new(262144);
        ulong scannedBytes = 0;
        foreach (PeSection section in metadata.Sections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!section.IsExecutable)
            {
                continue;
            }

            uint byteCount = section.RawSize > 0 ? section.RawSize : section.VirtualSize;
            if (byteCount == 0 || section.VirtualAddress >= metadata.SizeOfImage)
            {
                continue;
            }

            byteCount = Math.Min(byteCount, metadata.SizeOfImage - section.VirtualAddress);
            int length = checked((int)Math.Min(byteCount, int.MaxValue));
            byte[] bytes = new byte[length];
            ulong sectionAddress = module.BaseAddress + section.VirtualAddress;
            int bytesRead = process.ReadBytesPartial(sectionAddress, bytes);
            if (bytesRead <= 0)
            {
                scannedBytes += byteCount;
                continue;
            }

            ScanSection(
                bytes.AsSpan(0, bytesRead),
                section.VirtualAddress,
                metadata.Is64Bit,
                functions,
                records,
                scannedBytes,
                totalBytes,
                progress,
                cancellationToken);
            scannedBytes += byteCount;
        }

        return records;
    }

    private static void ScanSection(
        ReadOnlySpan<byte> bytes,
        uint sectionRva,
        bool is64Bit,
        FunctionEntry[] functions,
        List<XrefRecord> records,
        ulong scannedBefore,
        ulong totalBytes,
        IProgress<(double Progress, string Message)>? progress,
        CancellationToken cancellationToken)
    {
        if (bytes.IsEmpty)
        {
            return;
        }

        byte[] code = bytes.ToArray();
        ByteArrayCodeReader reader = new(code);
        Decoder decoder = Decoder.Create(is64Bit ? 64 : 32, reader);
        decoder.IP = sectionRva;
        ulong sectionEnd = sectionRva + (ulong)code.Length;
        int nextProgressAt = ProgressIntervalBytes;
        while (decoder.IP < sectionEnd)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Instruction instruction = decoder.Decode();
            if (instruction.IsInvalid)
            {
                ulong next = decoder.IP - (ulong)Math.Max(instruction.Length, 1) + 1;
                decoder.IP = next;
                reader.Position = checked((int)(next - sectionRva));
                continue;
            }

            uint sourceRva = checked((uint)instruction.IP);
            if (instruction.FlowControl == FlowControl.Call)
            {
                AddBranchRecord(
                    records,
                    functions,
                    sourceRva,
                    instruction.NearBranchTarget,
                    XrefKind.Call);
            }
            else if (instruction.FlowControl == FlowControl.UnconditionalBranch)
            {
                uint target = checked((uint)instruction.NearBranchTarget);
                FunctionEntry? caller = FindFunctionContaining(functions, sourceRva);
                bool tailCall = caller is null ||
                    target < caller.Value.StartRva ||
                    target >= caller.Value.EndRva;
                AddBranchRecord(
                    records,
                    functions,
                    sourceRva,
                    instruction.NearBranchTarget,
                    tailCall ? XrefKind.TailCall : XrefKind.Jump);
            }
            else if (instruction.Mnemonic == Mnemonic.Lea &&
                     instruction.IsIPRelativeMemoryOperand)
            {
                AddBranchRecord(
                    records,
                    functions,
                    sourceRva,
                    instruction.IPRelativeMemoryAddress,
                    XrefKind.Lea);
            }

            int scanned = checked((int)(decoder.IP - sectionRva));
            if (progress is not null &&
                scanned >= nextProgressAt &&
                totalBytes != 0)
            {
                nextProgressAt += ProgressIntervalBytes;
                double ratio = Math.Clamp(
                    0.15 + 0.8 * ((scannedBefore + (ulong)scanned) / (double)totalBytes),
                    0.15,
                    0.95);
                progress.Report((
                    ratio,
                    $"正在反汇编... {(scannedBefore + (ulong)scanned) / 1024 / 1024:N0}MB / " +
                    $"{totalBytes / 1024 / 1024:N0}MB"));
            }
        }
    }

    private static void AddBranchRecord(
        List<XrefRecord> records,
        FunctionEntry[] functions,
        uint sourceRva,
        ulong targetAddress,
        XrefKind kind)
    {
        if (targetAddress == 0 || targetAddress > uint.MaxValue)
        {
            return;
        }

        uint targetRva = (uint)targetAddress;
        if (targetRva == sourceRva)
        {
            return;
        }

        _ = functions;
        records.Add(new XrefRecord(sourceRva, targetRva, kind));
    }

    private static FunctionEntry? FindFunctionContaining(
        FunctionEntry[] functions,
        uint rva)
    {
        int low = 0;
        int high = functions.Length - 1;
        int candidate = -1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            if (functions[middle].StartRva <= rva)
            {
                candidate = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        if (candidate >= 0)
        {
            FunctionEntry function = functions[candidate];
            if (function.EndRva > function.StartRva && rva < function.EndRva)
            {
                return function;
            }
        }

        return null;
    }

    private static XrefDatabase BuildDatabase(
        ModuleDescriptor module,
        PeModuleMetadata metadata,
        FunctionEntry[] functions,
        List<XrefRecord> sourceRecords,
        TimeSpan duration)
    {
        HashSet<XrefRecord> unique = new(sourceRecords.Count);
        foreach (XrefRecord record in sourceRecords)
        {
            unique.Add(record);
        }

        XrefRecord[] byTarget = unique
            .OrderBy(static record => record.TargetRva)
            .ThenBy(static record => record.SourceRva)
            .ThenBy(static record => record.Kind)
            .ToArray();
        int[] bySourceIndex = Enumerable.Range(0, byTarget.Length)
            .OrderBy(index => byTarget[index].SourceRva)
            .ThenBy(index => byTarget[index].TargetRva)
            .ToArray();
        return XrefDatabase.CreateModuleDatabase(
            module.FilePath,
            module.Name,
            module.BaseAddress,
            metadata.Is64Bit ? 64 : 32,
            byTarget,
            bySourceIndex,
            functions,
            duration);
    }

    private static XrefKind Classify(InstructionSnapshot instruction)
    {
        if (instruction.IsCall)
        {
            return XrefKind.Call;
        }

        if (instruction.IsJump ||
            string.Equals(
                instruction.FlowControl,
                "ConditionalBranch",
                StringComparison.Ordinal))
        {
            return XrefKind.Jump;
        }

        return XrefKind.Address;
    }
}
