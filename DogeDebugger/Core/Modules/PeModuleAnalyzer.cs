using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace DogeDebugger.Core.Modules;

public sealed class PeModuleAnalyzer
{
    public PeModuleMetadata AnalyzeFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        byte[] bytes = File.ReadAllBytes(path);
        return Analyze(bytes, path);
    }

    public PeModuleMetadata Analyze(byte[] bytes, string filePath)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length < 0x100 ||
            bytes[0] != (byte)'M' ||
            bytes[1] != (byte)'Z')
        {
            throw new InvalidDataException("The file is not a PE image.");
        }

        int peOffset = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0x3C, 4));
        if (!IsRangeValid(bytes, peOffset, 24) ||
            bytes[peOffset] != (byte)'P' ||
            bytes[peOffset + 1] != (byte)'E' ||
            bytes[peOffset + 2] != 0 ||
            bytes[peOffset + 3] != 0)
        {
            throw new InvalidDataException("The PE signature is invalid.");
        }

        ushort machine = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(peOffset + 4, 2));
        int sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(peOffset + 6, 2));
        ushort characteristics = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(peOffset + 18, 2));
        int optionalHeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(peOffset + 20, 2));
        int optionalHeaderOffset = peOffset + 24;
        if (!IsRangeValid(bytes, optionalHeaderOffset, optionalHeaderSize))
        {
            throw new InvalidDataException("The PE optional header is truncated.");
        }

        ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(optionalHeaderOffset, 2));
        bool is64Bit = magic switch
        {
            0x10B => false,
            0x20B => true,
            _ => throw new InvalidDataException($"Unsupported PE optional header magic 0x{magic:X4}.")
        };

        ulong imageBase = is64Bit
            ? BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(optionalHeaderOffset + 24, 8))
            : BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(optionalHeaderOffset + 28, 4));
        uint sizeOfImage = BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(optionalHeaderOffset + 56, 4));
        uint sizeOfHeaders = BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(optionalHeaderOffset + 60, 4));
        uint entryPointRva = BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(optionalHeaderOffset + 16, 4));
        ushort subsystem = BinaryPrimitives.ReadUInt16LittleEndian(
            bytes.AsSpan(optionalHeaderOffset + (is64Bit ? 68 : 68), 2));
        uint dataDirectoryCount = BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(optionalHeaderOffset + (is64Bit ? 108 : 92), 4));
        int dataDirectoryOffset = optionalHeaderOffset + (is64Bit ? 112 : 96);

        List<PeSection> sections = ReadSections(
            bytes,
            optionalHeaderOffset + optionalHeaderSize,
            sectionCount);
        uint timestamp = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(peOffset + 8, 4));

        List<PeImport> imports = dataDirectoryCount > 1
            ? ReadImports(bytes, sections, ReadDirectoryRva(bytes, dataDirectoryOffset, 1), is64Bit)
            : [];
        (List<PeExport> exports, uint exportDirectoryRva, uint exportDirectorySize) =
            dataDirectoryCount > 0
                ? ReadExports(bytes, sections, ReadDirectoryRva(bytes, dataDirectoryOffset, 0))
                : ([], 0, 0);
        (uint exceptionDirectoryRva, uint exceptionDirectorySize) =
            dataDirectoryCount > 3
                ? ReadDirectory(bytes, dataDirectoryOffset, 3)
                : (0U, 0U);
        List<PeRuntimeFunction> runtimeFunctions = ReadRuntimeFunctions(
            bytes,
            sections,
            exceptionDirectoryRva,
            exceptionDirectorySize,
            is64Bit);

        if (exportDirectoryRva != 0)
        {
            exports = ResolveForwarders(
                bytes,
                sections,
                exports,
                exportDirectoryRva,
                exportDirectorySize);
        }

        return new PeModuleMetadata
        {
            FilePath = filePath,
            Machine = machine,
            Characteristics = characteristics,
            Is64Bit = is64Bit,
            ImageBase = imageBase,
            SizeOfImage = sizeOfImage,
            SizeOfHeaders = sizeOfHeaders,
            EntryPointRva = entryPointRva,
            Timestamp = timestamp,
            Subsystem = subsystem,
            Sections = sections,
            Imports = imports,
            Exports = exports,
            RuntimeFunctions = runtimeFunctions
        };
    }

    private static List<PeSection> ReadSections(
        byte[] bytes,
        int sectionOffset,
        int sectionCount)
    {
        List<PeSection> sections = new(sectionCount);
        for (int index = 0; index < sectionCount; index++)
        {
            int offset = sectionOffset + index * 40;
            if (!IsRangeValid(bytes, offset, 40))
            {
                break;
            }

            string name = Encoding.ASCII
                .GetString(bytes, offset, 8)
                .TrimEnd('\0');
            sections.Add(new PeSection
            {
                Name = name,
                VirtualSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 8, 4)),
                VirtualAddress = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 12, 4)),
                RawSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 16, 4)),
                RawOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 20, 4)),
                Characteristics = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 36, 4))
            });
        }

        return sections;
    }

    private static List<PeImport> ReadImports(
        byte[] bytes,
        IReadOnlyList<PeSection> sections,
        uint importDirectoryRva,
        bool is64Bit)
    {
        List<PeImport> imports = [];
        if (importDirectoryRva == 0 ||
            !TryRvaToOffset(sections, importDirectoryRva, out int descriptorOffset))
        {
            return imports;
        }

        int thunkSize = is64Bit ? 8 : 4;
        for (int descriptorIndex = 0; descriptorIndex < 4096; descriptorIndex++)
        {
            int offset = descriptorOffset + descriptorIndex * 20;
            if (!IsRangeValid(bytes, offset, 20))
            {
                break;
            }

            uint originalFirstThunk = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
            uint nameRva = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 12, 4));
            uint firstThunk = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 16, 4));
            if (originalFirstThunk == 0 && nameRva == 0 && firstThunk == 0)
            {
                break;
            }

            string moduleName = ReadAsciiAtRva(bytes, sections, nameRva);
            uint thunkRva = originalFirstThunk != 0 ? originalFirstThunk : firstThunk;
            if (thunkRva == 0)
            {
                imports.Add(new PeImport
                {
                    ModuleName = moduleName,
                    IatRva = firstThunk
                });
                continue;
            }

            for (int thunkIndex = 0; thunkIndex < 1_000_000; thunkIndex++)
            {
                uint currentRva = thunkRva + (uint)(thunkIndex * thunkSize);
                if (!TryRvaToOffset(sections, currentRva, out int thunkOffset) ||
                    !IsRangeValid(bytes, thunkOffset, thunkSize))
                {
                    break;
                }

                ulong thunk = is64Bit
                    ? BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(thunkOffset, 8))
                    : BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(thunkOffset, 4));
                if (thunk == 0)
                {
                    break;
                }

                ulong ordinalMask = is64Bit ? 0x8000000000000000UL : 0x80000000UL;
                if ((thunk & ordinalMask) != 0)
                {
                    imports.Add(new PeImport
                    {
                        ModuleName = moduleName,
                        Ordinal = (ushort)(thunk & 0xFFFF),
                        IatRva = firstThunk + (uint)(thunkIndex * thunkSize)
                    });
                    continue;
                }

                uint hintNameRva = checked((uint)thunk);
                string functionName = TryRvaToOffset(
                    sections,
                    hintNameRva,
                    out int hintNameOffset) &&
                    IsRangeValid(bytes, hintNameOffset + 2, 1)
                        ? ReadAscii(bytes, hintNameOffset + 2)
                        : string.Empty;
                imports.Add(new PeImport
                {
                    ModuleName = moduleName,
                    FunctionName = functionName,
                    IatRva = firstThunk + (uint)(thunkIndex * thunkSize)
                });
            }
        }

        return imports;
    }

    private static (
        List<PeExport> Exports,
        uint DirectoryRva,
        uint DirectorySize) ReadExports(
        byte[] bytes,
        IReadOnlyList<PeSection> sections,
        uint exportDirectoryRva)
    {
        List<PeExport> exports = [];
        if (exportDirectoryRva == 0 ||
            !TryRvaToOffset(sections, exportDirectoryRva, out int directoryOffset) ||
            !IsRangeValid(bytes, directoryOffset, 40))
        {
            return (exports, 0, 0);
        }

        uint ordinalBase = BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(directoryOffset + 16, 4));
        uint functionCount = BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(directoryOffset + 20, 4));
        uint nameCount = BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(directoryOffset + 24, 4));
        uint functionsRva = BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(directoryOffset + 28, 4));
        uint namesRva = BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(directoryOffset + 32, 4));
        uint ordinalsRva = BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(directoryOffset + 36, 4));
        uint directorySize = BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(directoryOffset + 4, 4));

        Dictionary<uint, string> namesByOrdinal = [];
        for (uint index = 0; index < nameCount; index++)
        {
            if (!TryRvaToOffset(sections, namesRva + index * 4, out int namePointerOffset) ||
                !IsRangeValid(bytes, namePointerOffset, 4))
            {
                break;
            }

            uint nameRva = BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.AsSpan(namePointerOffset, 4));
            if (!TryRvaToOffset(sections, ordinalsRva + index * 2, out int ordinalOffset) ||
                !IsRangeValid(bytes, ordinalOffset, 2))
            {
                break;
            }

            ushort functionIndex = BinaryPrimitives.ReadUInt16LittleEndian(
                bytes.AsSpan(ordinalOffset, 2));
            namesByOrdinal[functionIndex] = ReadAsciiAtRva(bytes, sections, nameRva);
        }

        for (uint index = 0; index < functionCount; index++)
        {
            if (!TryRvaToOffset(sections, functionsRva + index * 4, out int functionOffset) ||
                !IsRangeValid(bytes, functionOffset, 4))
            {
                break;
            }

            uint functionRva = BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.AsSpan(functionOffset, 4));
            if (functionRva == 0)
            {
                continue;
            }

            string name = namesByOrdinal.TryGetValue(index, out string? value)
                ? value
                : $"#{ordinalBase + index}";
            exports.Add(new PeExport
            {
                Name = name,
                Ordinal = ordinalBase + index,
                FunctionRva = functionRva
            });
        }

        return (exports, exportDirectoryRva, directorySize);
    }

    private static List<PeExport> ResolveForwarders(
        byte[] bytes,
        IReadOnlyList<PeSection> sections,
        List<PeExport> exports,
        uint exportDirectoryRva,
        uint exportDirectorySize)
    {
        uint end = exportDirectoryRva + exportDirectorySize;
        for (int index = 0; index < exports.Count; index++)
        {
            PeExport export = exports[index];
            if (export.FunctionRva < exportDirectoryRva || export.FunctionRva >= end)
            {
                continue;
            }

            exports[index] = new PeExport
            {
                Name = export.Name,
                Ordinal = export.Ordinal,
                FunctionRva = export.FunctionRva,
                ForwarderName = ReadAsciiAtRva(bytes, sections, export.FunctionRva)
            };
        }

        return exports;
    }

    private static uint ReadDirectoryRva(byte[] bytes, int dataDirectoryOffset, int index)
    {
        int offset = dataDirectoryOffset + index * 8;
        return IsRangeValid(bytes, offset, 4)
            ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4))
            : 0;
    }

    private static (uint Rva, uint Size) ReadDirectory(
        byte[] bytes,
        int dataDirectoryOffset,
        int index)
    {
        int offset = dataDirectoryOffset + index * 8;
        return IsRangeValid(bytes, offset, 8)
            ? (
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4, 4)))
            : (0U, 0U);
    }

    private static List<PeRuntimeFunction> ReadRuntimeFunctions(
        byte[] bytes,
        IReadOnlyList<PeSection> sections,
        uint directoryRva,
        uint directorySize,
        bool is64Bit)
    {
        List<PeRuntimeFunction> functions = [];
        int entrySize = is64Bit ? 12 : 8;
        if (directoryRva == 0 ||
            directorySize < entrySize ||
            !TryRvaToOffset(sections, directoryRva, out int directoryOffset))
        {
            return functions;
        }

        int available = Math.Min(
            checked((int)Math.Min(directorySize, int.MaxValue)),
            bytes.Length - directoryOffset);
        int count = available / entrySize;
        for (int index = 0; index < count; index++)
        {
            int offset = directoryOffset + index * entrySize;
            uint beginRva = BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.AsSpan(offset, 4));
            uint endRva = BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.AsSpan(offset + 4, 4));
            if (beginRva == 0)
            {
                continue;
            }

            uint unwindInfoRva = is64Bit
                ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 8, 4))
                : 0;
            functions.Add(new PeRuntimeFunction
            {
                BeginRva = beginRva,
                EndRva = endRva,
                UnwindInfoRva = unwindInfoRva
            });
        }

        return functions;
    }

    private static bool TryRvaToOffset(
        IReadOnlyList<PeSection> sections,
        uint rva,
        out int offset)
    {
        foreach (PeSection section in sections)
        {
            uint size = Math.Max(section.VirtualSize, section.RawSize);
            if (rva >= section.VirtualAddress && rva < section.VirtualAddress + size)
            {
                offset = checked((int)(section.RawOffset + rva - section.VirtualAddress));
                return true;
            }
        }

        if (rva < 0x1000)
        {
            offset = checked((int)rva);
            return true;
        }

        offset = 0;
        return false;
    }

    private static string ReadAsciiAtRva(
        byte[] bytes,
        IReadOnlyList<PeSection> sections,
        uint rva)
    {
        return TryRvaToOffset(sections, rva, out int offset)
            ? ReadAscii(bytes, offset)
            : string.Empty;
    }

    private static string ReadAscii(byte[] bytes, int offset)
    {
        if (!IsRangeValid(bytes, offset, 1))
        {
            return string.Empty;
        }

        int end = offset;
        while (end < bytes.Length && bytes[end] != 0 && end - offset < 32_768)
        {
            end++;
        }

        return Encoding.ASCII.GetString(bytes, offset, end - offset);
    }

    private static bool IsRangeValid(byte[] bytes, int offset, int length) =>
        offset >= 0 && length >= 0 && offset <= bytes.Length - length;
}
