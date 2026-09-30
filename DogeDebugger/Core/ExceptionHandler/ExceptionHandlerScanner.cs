using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using DogeDebugger.Core.Disassembly;
using DogeDebugger.Core.Modules;
using DogeDebugger.Core.Process;

namespace DogeDebugger.Core.ExceptionHandler;

public sealed class ExceptionHandlerScanner
{
    private const int ProcessCookieInformationClass = 36;
    private const int MaximumVehNodes = 256;
    private const int MaximumPdataEntries = 10_000;
    private const int MaximumHookBytes = 16;
    private const ushort Pe32Magic = 0x10B;
    private const ushort Pe32PlusMagic = 0x20B;

    private readonly PeModuleAnalyzer _peAnalyzer = new();
    private readonly DisassemblerService _disassembler = new();

    public ExceptionHandlerScanResult Scan(
        ITargetProcess process,
        IReadOnlyList<ModuleDescriptor> modules,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(modules);
        cancellationToken.ThrowIfCancellationRequested();

        ModuleDescriptor? ntdll = modules.FirstOrDefault(module =>
            string.Equals(module.Name, "ntdll.dll", StringComparison.OrdinalIgnoreCase));
        if (ntdll is null)
        {
            return new ExceptionHandlerScanResult();
        }

        List<VehEntry> vehEntries = ScanVectoredHandlers(
            process,
            ntdll,
            modules,
            cancellationToken);
        List<HookDetectionEntry> hookEntries = ScanHookStubs(
            process,
            ntdll,
            cancellationToken);
        PdataScanResult pdata = ScanPdata(modules, cancellationToken);
        return new ExceptionHandlerScanResult
        {
            VehEntries = vehEntries,
            PdataEntries = pdata.Entries,
            HookEntries = hookEntries,
            PdataCandidateCount = pdata.CandidateCount,
            PdataTruncated = pdata.Truncated
        };
    }

    private List<VehEntry> ScanVectoredHandlers(
        ITargetProcess process,
        ModuleDescriptor ntdll,
        IReadOnlyList<ModuleDescriptor> modules,
        CancellationToken cancellationToken)
    {
        List<VehEntry> entries = [];
        uint? cookie = QueryProcessCookie(process.Handle);
        if (cookie is null ||
            !TryResolveExport(ntdll, "LdrpVectorHandlerList", out ulong listAddress))
        {
            return entries;
        }

        ScanVehList(
            process,
            listAddress + 8,
            cookie.Value,
            modules,
            VehEntryType.VectoredExceptionHandler,
            entries,
            cancellationToken);
        ScanVehList(
            process,
            listAddress + 32,
            cookie.Value,
            modules,
            VehEntryType.VectoredContinueHandler,
            entries,
            cancellationToken);

        for (int index = 0; index < entries.Count; index++)
        {
            entries[index].Index = index + 1;
        }

        return entries;
    }

    private static void ScanVehList(
        ITargetProcess process,
        ulong listHeadAddress,
        uint cookie,
        IReadOnlyList<ModuleDescriptor> modules,
        VehEntryType type,
        List<VehEntry> entries,
        CancellationToken cancellationToken)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        if (!process.TryReadBytes(listHeadAddress, buffer))
        {
            return;
        }

        ulong node = BinaryPrimitives.ReadUInt64LittleEndian(buffer);
        int count = 0;
        while (node != listHeadAddress && node != 0 && count < MaximumVehNodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            count++;
            if (!process.TryReadBytes(node + 32, buffer))
            {
                break;
            }

            ulong encoded = BinaryPrimitives.ReadUInt64LittleEndian(buffer);
            ulong decoded = DecodePointer(encoded, cookie);
            (string moduleName, string symbolName) = ResolveAddress(decoded, modules);
            entries.Add(new VehEntry
            {
                NodeAddress = node,
                EncodedPointer = encoded,
                DecodedAddress = decoded,
                ModuleName = moduleName,
                SymbolName = symbolName,
                Type = type
            });

            if (!process.TryReadBytes(node, buffer))
            {
                break;
            }

            node = BinaryPrimitives.ReadUInt64LittleEndian(buffer);
        }
    }

    private List<HookDetectionEntry> ScanHookStubs(
        ITargetProcess process,
        ModuleDescriptor ntdll,
        CancellationToken cancellationToken)
    {
        List<HookDetectionEntry> entries = [];
        ScanHookStub(
            process,
            ntdll,
            "KiUserExceptionDispatcher",
            expectedFirstByte: 0xFC,
            expectedInstruction: "cld",
            entries,
            cancellationToken);
        ScanHookStub(
            process,
            ntdll,
            "RtlDispatchException",
            expectedFirstByte: null,
            expectedInstruction: "push reg",
            entries,
            cancellationToken);
        return entries;
    }

    private void ScanHookStub(
        ITargetProcess process,
        ModuleDescriptor ntdll,
        string exportName,
        byte? expectedFirstByte,
        string expectedInstruction,
        List<HookDetectionEntry> entries,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryResolveExport(ntdll, exportName, out ulong address))
        {
            return;
        }

        byte[] bytes = process.ReadBytes(address, MaximumHookBytes);
        if (bytes.Length == 0)
        {
            return;
        }

        InstructionSnapshot? instruction = _disassembler.DisassembleOne(
            bytes,
            address,
            process.Is64Bit);
        if (instruction is null)
        {
            return;
        }

        bool hooked = expectedFirstByte is { } expected
            ? bytes[0] != expected
            : bytes[0] is < 0x50 or > 0x57;
        int actualLength = Math.Clamp(instruction.Length, 1, bytes.Length);
        entries.Add(new HookDetectionEntry
        {
            FunctionName = exportName,
            Address = address,
            ExpectedInstruction = expectedInstruction,
            ActualInstruction = instruction.Text,
            ActualBytes = Convert.ToHexString(bytes.AsSpan(0, actualLength)),
            IsHooked = hooked,
            Status = hooked ? "已被 Hook" : "正常"
        });
    }

    private PdataScanResult ScanPdata(
        IReadOnlyList<ModuleDescriptor> modules,
        CancellationToken cancellationToken)
    {
        List<PdataSehEntry> entries = [];
        int candidateCount = 0;
        bool truncated = false;
        foreach (ModuleDescriptor module in modules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(module.FilePath) ||
                !File.Exists(module.FilePath))
            {
                continue;
            }

            try
            {
                byte[] image = File.ReadAllBytes(module.FilePath);
                if (!TryReadPeLayout(image, out PeLayout layout))
                {
                    continue;
                }

                if (!TryReadDataDirectory(
                        image,
                        layout,
                        directoryIndex: 3,
                        out uint exceptionRva,
                        out uint exceptionSize) ||
                    exceptionRva == 0 ||
                    exceptionSize == 0)
                {
                    continue;
                }

                int entrySize = layout.Is64Bit ? 12 : 8;
                uint count = Math.Min(
                    exceptionSize / (uint)entrySize,
                    MaximumPdataEntries + 1U);
                for (uint index = 0; index < count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    uint entryRva = exceptionRva + index * (uint)entrySize;
                    if (!TryRvaToOffset(layout, entryRva, entrySize, out int entryOffset))
                    {
                        break;
                    }

                    if (entryOffset < 0 || entryOffset + entrySize > image.Length)
                    {
                        break;
                    }

                    ReadOnlySpan<byte> entry = image.AsSpan(entryOffset, entrySize);
                    uint beginRva = BinaryPrimitives.ReadUInt32LittleEndian(entry);
                    uint endRva = BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);
                    uint unwindRva = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
                    if (beginRva == 0 || endRva <= beginRva || unwindRva == 0)
                    {
                        continue;
                    }

                    if (!TryRvaToOffset(layout, unwindRva, 8, out int unwindOffset) ||
                        unwindOffset + 4 > image.Length)
                    {
                        continue;
                    }

                    byte versionAndFlags = image[unwindOffset];
                    byte flags = (byte)(versionAndFlags & 0x7);
                    if ((flags & 0x3) == 0)
                    {
                        continue;
                    }

                    candidateCount++;
                    if (entries.Count >= MaximumPdataEntries)
                    {
                        truncated = true;
                        continue;
                    }

                    byte countOfCodes = image[unwindOffset + 1];
                    int handlerOffset = unwindOffset + 4 + countOfCodes * 2;
                    uint handlerRva = ReadHandlerRva(image, unwindOffset, handlerOffset);
                    if (handlerRva == 0)
                    {
                        continue;
                    }

                    ulong handlerAddress = module.BaseAddress + handlerRva;
                    entries.Add(new PdataSehEntry
                    {
                        Index = entries.Count + 1,
                        FunctionStart = module.BaseAddress + beginRva,
                        FunctionEnd = module.BaseAddress + endRva,
                        HandlerAddress = handlerAddress,
                        ModuleName = module.Name,
                        HandlerSymbol = ResolveAddress(handlerAddress, modules).SymbolName,
                        Flags = flags
                    });
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (InvalidDataException)
            {
            }
        }

        return new PdataScanResult(entries, candidateCount, truncated);
    }

    private bool TryResolveExport(
        ModuleDescriptor module,
        string exportName,
        out ulong address)
    {
        address = 0;
        if (string.IsNullOrWhiteSpace(module.FilePath) || !File.Exists(module.FilePath))
        {
            return false;
        }

        try
        {
            PeModuleMetadata metadata = _peAnalyzer.AnalyzeFile(module.FilePath);
            PeExport? export = metadata.Exports.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, exportName, StringComparison.Ordinal) &&
                string.IsNullOrWhiteSpace(candidate.ForwarderName));
            if (export is null || export.FunctionRva == 0)
            {
                return false;
            }

            address = module.BaseAddress + export.FunctionRva;
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static (string ModuleName, string SymbolName) ResolveAddress(
        ulong address,
        IReadOnlyList<ModuleDescriptor> modules)
    {
        ModuleDescriptor? module = ModuleCatalog.FindByAddress(modules, address);
        if (module is null)
        {
            return ("???", $"0x{address:X}");
        }

        ulong moduleOffset = address - module.BaseAddress;
        try
        {
            PeModuleAnalyzer analyzer = new();
            PeModuleMetadata metadata = analyzer.AnalyzeFile(module.FilePath);
            PeExport? nearest = null;
            ulong nearestOffset = ulong.MaxValue;
            foreach (PeExport export in metadata.Exports)
            {
                if (!string.IsNullOrWhiteSpace(export.ForwarderName) ||
                    export.FunctionRva == 0)
                {
                    continue;
                }

                ulong exportAddress = module.BaseAddress + export.FunctionRva;
                if (exportAddress > address)
                {
                    continue;
                }

                ulong delta = address - exportAddress;
                if (delta < nearestOffset)
                {
                    nearest = export;
                    nearestOffset = delta;
                }
            }

            if (nearest is not null && nearestOffset < 0x10000)
            {
                string symbol = nearestOffset == 0
                    ? nearest.Name
                    : $"{nearest.Name}+0x{nearestOffset:X}";
                return (module.Name, symbol);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (InvalidDataException)
        {
        }

        return (module.Name, $"+0x{moduleOffset:X}");
    }

    private static uint? QueryProcessCookie(IntPtr processHandle)
    {
        int status = NtQueryInformationProcess(
            processHandle,
            ProcessCookieInformationClass,
            out uint cookie,
            sizeof(uint),
            out _);
        return status >= 0 ? cookie : null;
    }

    private static ulong DecodePointer(ulong encoded, uint cookie)
    {
        uint shift = cookie & 63;
        return BitOperations.RotateRight(encoded, (int)(64 - shift)) ^ cookie;
    }

    private static bool TryReadPeLayout(byte[] image, out PeLayout layout)
    {
        layout = default;
        if (image.Length < 0x40 ||
            image[0] != (byte)'M' ||
            image[1] != (byte)'Z')
        {
            return false;
        }

        int peOffset = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(0x3C, 4));
        if (peOffset < 0 ||
            peOffset + 24 > image.Length ||
            image[peOffset] != (byte)'P' ||
            image[peOffset + 1] != (byte)'E' ||
            image[peOffset + 2] != 0 ||
            image[peOffset + 3] != 0)
        {
            return false;
        }

        ushort machine = BinaryPrimitives.ReadUInt16LittleEndian(
            image.AsSpan(peOffset + 4, 2));
        ushort sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(
            image.AsSpan(peOffset + 6, 2));
        ushort optionalHeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(
            image.AsSpan(peOffset + 20, 2));
        int optionalHeaderOffset = peOffset + 24;
        if (optionalHeaderOffset + optionalHeaderSize > image.Length ||
            optionalHeaderSize < 2)
        {
            return false;
        }

        ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(
            image.AsSpan(optionalHeaderOffset, 2));
        int dataDirectoryOffset = magic switch
        {
            Pe32Magic => optionalHeaderOffset + 96,
            Pe32PlusMagic => optionalHeaderOffset + 112,
            _ => -1
        };
        if (dataDirectoryOffset < 0 ||
            dataDirectoryOffset + 8 * 8 > image.Length)
        {
            return false;
        }

        uint numberOfDirectories = BinaryPrimitives.ReadUInt32LittleEndian(
            image.AsSpan(dataDirectoryOffset - 4, 4));
        int sectionTableOffset = optionalHeaderOffset + optionalHeaderSize;
        int sectionTableSize = sectionCount * 40;
        if (sectionTableOffset < 0 ||
            sectionTableSize < 0 ||
            sectionTableOffset + sectionTableSize > image.Length)
        {
            return false;
        }

        List<PeSectionLayout> sections = new(sectionCount);
        for (int index = 0; index < sectionCount; index++)
        {
            int offset = sectionTableOffset + index * 40;
            sections.Add(new PeSectionLayout(
                BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(offset + 12, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(offset + 8, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(offset + 20, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(offset + 16, 4))));
        }

        layout = new PeLayout(
            machine == 0x8664,
            dataDirectoryOffset,
            numberOfDirectories,
            sections);
        return true;
    }

    private static bool TryReadDataDirectory(
        byte[] image,
        PeLayout layout,
        int directoryIndex,
        out uint rva,
        out uint size)
    {
        rva = 0;
        size = 0;
        if ((uint)directoryIndex >= layout.NumberOfDirectories)
        {
            return false;
        }

        int offset = layout.DataDirectoryOffset + directoryIndex * 8;
        if (offset < 0 || offset + 8 > image.Length)
        {
            return false;
        }

        rva = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(offset, 4));
        size = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(offset + 4, 4));
        return true;
    }

    private static bool TryRvaToOffset(
        PeLayout layout,
        uint rva,
        int length,
        out int offset)
    {
        foreach (PeSectionLayout section in layout.Sections)
        {
            uint span = Math.Max(section.VirtualSize, section.RawSize);
            if (rva < section.VirtualAddress ||
                rva >= section.VirtualAddress + span)
            {
                continue;
            }

            uint delta = rva - section.VirtualAddress;
            ulong raw = section.RawOffset + delta;
            if (raw > int.MaxValue)
            {
                break;
            }

            offset = (int)raw;
            return offset >= 0 && offset + length >= offset;
        }

        offset = -1;
        return false;
    }

    private static uint ReadHandlerRva(
        byte[] image,
        int unwindOffset,
        int handlerOffset)
    {
        int alignedOffset = (handlerOffset + 3) & ~3;
        foreach (int candidateOffset in new[] { handlerOffset, alignedOffset })
        {
            if (candidateOffset < 0 || candidateOffset + 4 > image.Length)
            {
                continue;
            }

            uint candidate = BinaryPrimitives.ReadUInt32LittleEndian(
                image.AsSpan(candidateOffset, 4));
            if (candidate != 0 && candidate < image.Length)
            {
                return candidate;
            }
        }

        return 0;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        out uint processInformation,
        uint processInformationLength,
        out uint returnLength);

    private readonly record struct PeLayout(
        bool Is64Bit,
        int DataDirectoryOffset,
        uint NumberOfDirectories,
        IReadOnlyList<PeSectionLayout> Sections);

    private readonly record struct PeSectionLayout(
        uint VirtualSize,
        uint VirtualAddress,
        uint RawSize,
        uint RawOffset);

    private readonly record struct PdataScanResult(
        IReadOnlyList<PdataSehEntry> Entries,
        int CandidateCount,
        bool Truncated);
}
