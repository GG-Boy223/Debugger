using System.Buffers.Binary;
using System.IO;
using System.Text;
using DogeDebugger.Core.Modules;
using DogeDebugger.Core.Process;

namespace DogeDebugger.Core.Signatures.CrossVersion;

public sealed class LoadedPeImage
{
    private LoadedPeImage(
        string name,
        string filePath,
        byte[] imageBytes,
        PeModuleMetadata metadata,
        IReadOnlyList<PeExport> exports)
    {
        Name = name;
        FilePath = filePath;
        ImageBytes = imageBytes;
        Metadata = metadata;
        Exports = exports;
    }

    public string Name { get; }

    public string FilePath { get; }

    public byte[] ImageBytes { get; }

    public PeModuleMetadata Metadata { get; }

    public IReadOnlyList<PeExport> Exports { get; }

    public bool Is64Bit => Metadata.Is64Bit;

    public ulong ImageBase => Metadata.ImageBase;

    public PeSection? FindSection(uint rva) =>
        Metadata.Sections.FirstOrDefault(section =>
            rva >= section.VirtualAddress &&
            rva < section.VirtualAddress +
            Math.Max(section.VirtualSize, section.RawSize));

    public bool IsExecutableRva(uint rva) =>
        FindSection(rva)?.IsExecutable == true;

    public bool IsWritableRva(uint rva) =>
        FindSection(rva)?.IsWritable == true;

    public bool TryRvaToOffset(uint rva, out int offset)
    {
        offset = 0;
        if (rva < Metadata.SizeOfHeaders && rva < ImageBytes.Length)
        {
            offset = checked((int)rva);
            return true;
        }

        PeSection? section = FindSection(rva);
        if (section is null)
        {
            return false;
        }

        uint relative = rva - section.VirtualAddress;
        if (relative >= Math.Max(section.VirtualSize, section.RawSize) ||
            section.VirtualAddress + relative >= ImageBytes.Length)
        {
            return false;
        }

        offset = checked((int)(section.VirtualAddress + relative));
        return true;
    }

    public bool TryReadBytes(uint rva, Span<byte> destination)
    {
        if (!TryRvaToOffset(rva, out int offset) ||
            offset + destination.Length > ImageBytes.Length)
        {
            destination.Clear();
            return false;
        }

        ImageBytes.AsSpan(offset, destination.Length).CopyTo(destination);
        return true;
    }

    public byte[] ReadBytes(uint rva, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (length == 0)
        {
            return [];
        }

        byte[] buffer = new byte[length];
        if (!TryReadBytes(rva, buffer))
        {
            Array.Resize(ref buffer, 0);
        }

        return buffer;
    }

    public string ReadAscii(uint rva, int maximumLength = 256)
    {
        if (!TryRvaToOffset(rva, out int offset) || offset >= ImageBytes.Length)
        {
            return string.Empty;
        }

        int end = offset;
        int limit = Math.Min(ImageBytes.Length, offset + maximumLength);
        while (end < limit && ImageBytes[end] != 0)
        {
            end++;
        }

        return Encoding.ASCII.GetString(ImageBytes, offset, end - offset);
    }

    public string ReadUnicode(uint rva, int maximumCharacters = 256)
    {
        if (!TryRvaToOffset(rva, out int offset) || offset >= ImageBytes.Length)
        {
            return string.Empty;
        }

        int end = offset;
        int limit = Math.Min(ImageBytes.Length, offset + maximumCharacters * 2);
        while (end + 1 < limit &&
               (ImageBytes[end] != 0 || ImageBytes[end + 1] != 0))
        {
            end += 2;
        }

        return Encoding.Unicode.GetString(ImageBytes, offset, end - offset);
    }

    public PeExport? FindExportByName(string name) =>
        Exports.FirstOrDefault(export =>
            export.Name.Equals(name, StringComparison.Ordinal));

    public PeExport? FindExportByRva(uint rva) =>
        Exports
            .Where(export =>
                export.ForwarderName is null &&
                export.FunctionRva == rva)
            .OrderBy(export => export.Name, StringComparer.Ordinal)
            .FirstOrDefault();

    public static LoadedPeImage FromFile(string path)
    {
        string fullPath = Path.GetFullPath(path);
        byte[] fileBytes = File.ReadAllBytes(fullPath);
        PeModuleMetadata metadata = new PeModuleAnalyzer().Analyze(
            fileBytes,
            fullPath);
        uint imageSize = GetImageSize(metadata);
        byte[] image = new byte[imageSize];
        int headerLength = checked((int)Math.Min(
            metadata.SizeOfHeaders == 0
                ? Math.Min((uint)fileBytes.Length, 0x1000)
                : metadata.SizeOfHeaders,
            (uint)fileBytes.Length));
        fileBytes.AsSpan(0, headerLength).CopyTo(image);
        foreach (PeSection section in metadata.Sections)
        {
            int destinationOffset = checked((int)section.VirtualAddress);
            if (destinationOffset >= image.Length || section.RawSize == 0)
            {
                continue;
            }

            int copyLength = checked((int)Math.Min(
                section.RawSize,
                Math.Min(
                    (uint)(image.Length - destinationOffset),
                    (uint)Math.Max(0, fileBytes.Length - checked((int)section.RawOffset)))));
            if (copyLength <= 0)
            {
                continue;
            }

            fileBytes.AsSpan(
                    checked((int)section.RawOffset),
                    copyLength)
                .CopyTo(image.AsSpan(destinationOffset));
        }

        IReadOnlyList<PeExport> exports = ReadLoadedExports(image, metadata);
        return new LoadedPeImage(
            Path.GetFileName(fullPath),
            fullPath,
            image,
            metadata,
            exports);
    }

    public static LoadedPeImage FromProcessModule(
        ITargetProcess process,
        ModuleDescriptor module)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(module);
        if (!process.IsOpen)
        {
            throw new InvalidOperationException("目标进程未打开。");
        }

        byte[] headers = new byte[0x1000];
        if (!process.TryReadBytes(module.BaseAddress, headers))
        {
            throw new InvalidOperationException("无法读取目标模块头部。");
        }

        if (headers.Length < 0x40 ||
            headers[0] != (byte)'M' ||
            headers[1] != (byte)'Z')
        {
            throw new InvalidDataException("目标模块不是有效的 PE 映像。");
        }

        int peOffset = BinaryPrimitives.ReadInt32LittleEndian(headers.AsSpan(0x3C, 4));
        if (peOffset <= 0 ||
            peOffset + 24 > headers.Length ||
            headers[peOffset] != (byte)'P' ||
            headers[peOffset + 1] != (byte)'E')
        {
            throw new InvalidDataException("目标模块 PE 签名无效。");
        }

        ushort machine = BinaryPrimitives.ReadUInt16LittleEndian(
            headers.AsSpan(peOffset + 4, 2));
        ushort characteristics = BinaryPrimitives.ReadUInt16LittleEndian(
            headers.AsSpan(peOffset + 18, 2));
        ushort sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(
            headers.AsSpan(peOffset + 6, 2));
        ushort optionalHeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(
            headers.AsSpan(peOffset + 20, 2));
        int optionalHeaderOffset = peOffset + 24;
        if (optionalHeaderOffset + optionalHeaderSize > headers.Length)
        {
            throw new InvalidDataException("目标模块可选头被截断。");
        }

        ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(
            headers.AsSpan(optionalHeaderOffset, 2));
        bool is64Bit = magic switch
        {
            0x10B => false,
            0x20B => true,
            _ => throw new InvalidDataException(
                $"不支持的可选头格式 0x{magic:X4}。")
        };
        uint sizeOfImage = BinaryPrimitives.ReadUInt32LittleEndian(
            headers.AsSpan(optionalHeaderOffset + 56, 4));
        uint sizeOfHeaders = BinaryPrimitives.ReadUInt32LittleEndian(
            headers.AsSpan(optionalHeaderOffset + 60, 4));
        ulong imageBase = is64Bit
            ? BinaryPrimitives.ReadUInt64LittleEndian(
                headers.AsSpan(optionalHeaderOffset + 24, 8))
            : BinaryPrimitives.ReadUInt32LittleEndian(
                headers.AsSpan(optionalHeaderOffset + 28, 4));
        uint entryPointRva = BinaryPrimitives.ReadUInt32LittleEndian(
            headers.AsSpan(optionalHeaderOffset + 16, 4));
        ushort subsystem = BinaryPrimitives.ReadUInt16LittleEndian(
            headers.AsSpan(optionalHeaderOffset + 68, 2));
        uint timestamp = BinaryPrimitives.ReadUInt32LittleEndian(
            headers.AsSpan(peOffset + 8, 4));

        List<PeSection> sections = [];
        int sectionOffset = optionalHeaderOffset + optionalHeaderSize;
        for (int index = 0; index < sectionCount; index++)
        {
            int offset = sectionOffset + index * 40;
            if (offset < 0 || offset + 40 > headers.Length)
            {
                break;
            }

            sections.Add(new PeSection
            {
                Name = Encoding.ASCII
                    .GetString(headers, offset, 8)
                    .TrimEnd('\0'),
                VirtualSize = BinaryPrimitives.ReadUInt32LittleEndian(
                    headers.AsSpan(offset + 8, 4)),
                VirtualAddress = BinaryPrimitives.ReadUInt32LittleEndian(
                    headers.AsSpan(offset + 12, 4)),
                RawSize = BinaryPrimitives.ReadUInt32LittleEndian(
                    headers.AsSpan(offset + 16, 4)),
                RawOffset = BinaryPrimitives.ReadUInt32LittleEndian(
                    headers.AsSpan(offset + 20, 4)),
                Characteristics = BinaryPrimitives.ReadUInt32LittleEndian(
                    headers.AsSpan(offset + 36, 4))
            });
        }

        uint imageSize = Math.Max(
            sizeOfImage,
            sections.Count == 0
                ? 0
                : sections.Max(section =>
                    section.VirtualAddress +
                    Math.Max(section.VirtualSize, section.RawSize)));
        imageSize = Math.Max(imageSize, sizeOfHeaders);
        imageSize = Math.Max(imageSize, 0x1000);
        byte[] image = new byte[imageSize];
        headers.AsSpan(0, Math.Min(headers.Length, image.Length)).CopyTo(image);
        foreach (PeSection section in sections)
        {
            int destinationOffset = checked((int)section.VirtualAddress);
            int sectionLength = checked((int)Math.Max(
                section.VirtualSize,
                section.RawSize));
            if (destinationOffset >= image.Length || sectionLength <= 0)
            {
                continue;
            }

            sectionLength = Math.Min(sectionLength, image.Length - destinationOffset);
            ulong remoteAddress = module.BaseAddress + section.VirtualAddress;
            int offset = 0;
            while (offset < sectionLength)
            {
                int chunkLength = Math.Min(0x10000, sectionLength - offset);
                process.TryReadBytes(
                    remoteAddress + (ulong)offset,
                    image.AsSpan(destinationOffset + offset, chunkLength));
                offset += chunkLength;
            }
        }

        PeModuleMetadata metadata = new()
        {
            FilePath = module.FilePath,
            Machine = machine,
            Characteristics = characteristics,
            Is64Bit = is64Bit,
            ImageBase = imageBase,
            SizeOfImage = imageSize,
            SizeOfHeaders = sizeOfHeaders,
            EntryPointRva = entryPointRva,
            Timestamp = timestamp,
            Subsystem = subsystem,
            Sections = sections,
            Imports = [],
            Exports = []
        };
        IReadOnlyList<PeExport> exports = ReadLoadedExports(image, metadata);
        return new LoadedPeImage(
            module.Name,
            module.FilePath,
            image,
            metadata,
            exports);
    }

    private static uint GetImageSize(PeModuleMetadata metadata)
    {
        uint sectionEnd = metadata.Sections.Count == 0
            ? 0
            : metadata.Sections.Max(section =>
                section.VirtualAddress +
                Math.Max(section.VirtualSize, section.RawSize));
        return Math.Max(
            0x1000u,
            Math.Max(metadata.SizeOfImage, Math.Max(sectionEnd, metadata.SizeOfHeaders)));
    }

    private static IReadOnlyList<PeExport> ReadLoadedExports(
        byte[] image,
        PeModuleMetadata metadata)
    {
        int optionalHeaderOffset = GetOptionalHeaderOffset(image);
        if (optionalHeaderOffset < 0)
        {
            return [];
        }

        int dataDirectoryOffset = optionalHeaderOffset + (metadata.Is64Bit ? 112 : 96);
        if (dataDirectoryOffset + 8 > image.Length)
        {
            return [];
        }

        uint exportDirectoryRva = BinaryPrimitives.ReadUInt32LittleEndian(
            image.AsSpan(dataDirectoryOffset, 4));
        uint exportDirectorySize = BinaryPrimitives.ReadUInt32LittleEndian(
            image.AsSpan(dataDirectoryOffset + 4, 4));
        if (exportDirectoryRva == 0 ||
            !TryRvaToOffset(metadata, image.Length, exportDirectoryRva, out int exportOffset) ||
            exportOffset + 40 > image.Length)
        {
            return [];
        }

        uint numberOfFunctions = BinaryPrimitives.ReadUInt32LittleEndian(
            image.AsSpan(exportOffset + 20, 4));
        uint numberOfNames = BinaryPrimitives.ReadUInt32LittleEndian(
            image.AsSpan(exportOffset + 24, 4));
        uint functionsRva = BinaryPrimitives.ReadUInt32LittleEndian(
            image.AsSpan(exportOffset + 28, 4));
        uint namesRva = BinaryPrimitives.ReadUInt32LittleEndian(
            image.AsSpan(exportOffset + 32, 4));
        uint ordinalsRva = BinaryPrimitives.ReadUInt32LittleEndian(
            image.AsSpan(exportOffset + 36, 4));
        if (numberOfNames > 1_000_000 ||
            !TryRvaToOffset(metadata, image.Length, functionsRva, out int functionsOffset) ||
            !TryRvaToOffset(metadata, image.Length, namesRva, out int namesOffset) ||
            !TryRvaToOffset(metadata, image.Length, ordinalsRva, out int ordinalsOffset))
        {
            return [];
        }

        List<PeExport> exports = [];
        for (int index = 0; index < numberOfNames; index++)
        {
            int nameRvaOffset = namesOffset + index * 4;
            int ordinalOffset = ordinalsOffset + index * 2;
            if (nameRvaOffset + 4 > image.Length || ordinalOffset + 2 > image.Length)
            {
                break;
            }

            uint nameRva = BinaryPrimitives.ReadUInt32LittleEndian(
                image.AsSpan(nameRvaOffset, 4));
            ushort ordinalIndex = BinaryPrimitives.ReadUInt16LittleEndian(
                image.AsSpan(ordinalOffset, 2));
            if (ordinalIndex >= numberOfFunctions ||
                !TryRvaToOffset(metadata, image.Length, nameRva, out int nameOffset) ||
                nameOffset >= image.Length)
            {
                continue;
            }

            string name = ReadAscii(image, nameOffset);
            if (name.Length == 0)
            {
                continue;
            }

            int functionOffset = functionsOffset + ordinalIndex * 4;
            if (functionOffset + 4 > image.Length)
            {
                continue;
            }

            uint functionRva = BinaryPrimitives.ReadUInt32LittleEndian(
                image.AsSpan(functionOffset, 4));
            string? forwarder = null;
            if (functionRva >= exportDirectoryRva &&
                functionRva < exportDirectoryRva + exportDirectorySize &&
                TryRvaToOffset(metadata, image.Length, functionRva, out int forwarderOffset))
            {
                forwarder = ReadAscii(image, forwarderOffset);
            }

            exports.Add(new PeExport
            {
                Name = name,
                Ordinal = checked((uint)ordinalIndex + 1),
                FunctionRva = functionRva,
                ForwarderName = forwarder
            });
        }

        return exports
            .OrderBy(export => export.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static int GetOptionalHeaderOffset(byte[] image)
    {
        if (image.Length < 0x40)
        {
            return -1;
        }

        int peOffset = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(0x3C, 4));
        if (peOffset <= 0 || peOffset + 24 > image.Length)
        {
            return -1;
        }

        return peOffset + 24;
    }

    private static bool TryRvaToOffset(
        PeModuleMetadata metadata,
        int imageLength,
        uint rva,
        out int offset)
    {
        offset = 0;
        if (rva < metadata.SizeOfHeaders && rva < imageLength)
        {
            offset = checked((int)rva);
            return true;
        }

        PeSection? section = metadata.Sections.FirstOrDefault(candidate =>
            rva >= candidate.VirtualAddress &&
            rva < candidate.VirtualAddress +
            Math.Max(candidate.VirtualSize, candidate.RawSize));
        if (section is null)
        {
            return false;
        }

        uint relative = rva - section.VirtualAddress;
        long candidateOffset = section.VirtualAddress + relative;
        if (candidateOffset < 0 || candidateOffset >= imageLength)
        {
            return false;
        }

        offset = checked((int)candidateOffset);
        return true;
    }

    private static string ReadAscii(byte[] image, int offset)
    {
        int end = offset;
        while (end < image.Length && image[end] != 0 && end - offset < 4096)
        {
            end++;
        }

        return Encoding.ASCII.GetString(image, offset, end - offset);
    }
}
