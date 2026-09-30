using System.Buffers.Binary;
using System.IO;
using System.Text;
using DogeDebugger.Core.Modules;
using DogeDebugger.Core.Process;

namespace DogeDebugger.Core.Rtti;

public sealed class RttiScanner
{
    private const int MaximumTypes = 10_000;
    private const int MaximumBaseClasses = 256;
    private const int MaximumTypeNameLength = 4096;

    public RttiScanResult Scan(
        ITargetProcess process,
        IReadOnlyList<ModuleDescriptor> modules,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(modules);
        cancellationToken.ThrowIfCancellationRequested();

        List<RttiTypeInfo> types = [];
        int scannedModules = 0;
        int candidateTypes = 0;
        bool truncated = false;
        foreach (ModuleDescriptor module in modules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(module.FilePath) ||
                !File.Exists(module.FilePath))
            {
                continue;
            }

            scannedModules++;
            try
            {
                byte[] image = File.ReadAllBytes(module.FilePath);
                if (!PeFileView.TryCreate(image, out PeFileView view))
                {
                    continue;
                }

                List<TypeDescriptorCandidate> candidates = FindTypeDescriptors(view);
                candidateTypes += candidates.Count;
                foreach (TypeDescriptorCandidate candidate in candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (types.Count >= MaximumTypes)
                    {
                        truncated = true;
                        break;
                    }

                    foreach (RttiTypeInfo type in ResolveCompleteObjectLocators(
                                 view,
                                 module,
                                 candidate,
                                 cancellationToken))
                    {
                        if (!types.Any(existing =>
                                existing.CompleteObjectLocatorAddress ==
                                type.CompleteObjectLocatorAddress &&
                                string.Equals(
                                    existing.ModuleName,
                                    type.ModuleName,
                                    StringComparison.OrdinalIgnoreCase)))
                        {
                            types.Add(type);
                        }
                    }
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

        return new RttiScanResult
        {
            Types = types
                .OrderBy(type => type.ModuleName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(type => type.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            ScannedModuleCount = scannedModules,
            CandidateTypeCount = candidateTypes,
            Truncated = truncated
        };
    }

    private static List<TypeDescriptorCandidate> FindTypeDescriptors(PeFileView view)
    {
        List<TypeDescriptorCandidate> candidates = [];
        foreach (PeSectionView section in view.Sections)
        {
            int start = checked((int)section.RawOffset);
            int end = checked((int)Math.Min(
                (ulong)view.Image.Length,
                (ulong)start + section.RawSize));
            if (start < 0 || end <= start || end > view.Image.Length)
            {
                continue;
            }

            for (int offset = start; offset + 4 < end; offset++)
            {
                if (view.Image[offset] != (byte)'.' ||
                    view.Image[offset + 1] != (byte)'?' ||
                    view.Image[offset + 2] != (byte)'A')
                {
                    continue;
                }

                string decoratedName = ReadAscii(view.Image, offset, MaximumTypeNameLength);
                if (decoratedName.Length < 5 ||
                    decoratedName[3] is not ('V' or 'U' or 'W'))
                {
                    continue;
                }

                int nameOffset = offset;
                uint nameRva = view.OffsetToRva(nameOffset);
                if (nameRva < 16)
                {
                    continue;
                }

                uint typeDescriptorRva = nameRva - 16;
                if (!view.IsRangeValid(typeDescriptorRva, 16))
                {
                    continue;
                }

                candidates.Add(new TypeDescriptorCandidate(
                    typeDescriptorRva,
                    nameRva,
                    decoratedName));
            }
        }

        return candidates
            .GroupBy(candidate => candidate.TypeDescriptorRva)
            .Select(group => group.First())
            .ToList();
    }

    private static IEnumerable<RttiTypeInfo> ResolveCompleteObjectLocators(
        PeFileView view,
        ModuleDescriptor module,
        TypeDescriptorCandidate candidate,
        CancellationToken cancellationToken)
    {
        HashSet<uint> locatorRvas = [];
        foreach (PeSectionView section in view.Sections)
        {
            int start = checked((int)section.RawOffset);
            int end = checked((int)Math.Min(
                (ulong)view.Image.Length,
                (ulong)start + section.RawSize));
            if (start < 0 || end <= start || end > view.Image.Length)
            {
                continue;
            }

            for (int offset = start; offset + 4 <= end; offset++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                uint value = BinaryPrimitives.ReadUInt32LittleEndian(
                    view.Image.AsSpan(offset, 4));
                if (value != candidate.TypeDescriptorRva)
                {
                    continue;
                }

                uint valueRva = view.OffsetToRva(offset);
                if (valueRva < 12)
                {
                    continue;
                }

                uint locatorRva = valueRva - 12;
                if (locatorRvas.Add(locatorRva) &&
                    TryReadTypeInfo(
                        view,
                        module,
                        candidate,
                        locatorRva,
                        out RttiTypeInfo? type))
                {
                    yield return type!;
                }
            }
        }
    }

    private static bool TryReadTypeInfo(
        PeFileView view,
        ModuleDescriptor module,
        TypeDescriptorCandidate candidate,
        uint locatorRva,
        out RttiTypeInfo? type)
    {
        type = null;
        if (!view.TryReadUInt32(locatorRva, out uint signature) ||
            !view.TryReadUInt32(locatorRva + 4, out uint offset) ||
            !view.TryReadUInt32(locatorRva + 8, out uint constructorDisplacement) ||
            !view.TryReadUInt32(locatorRva + 12, out uint typeDescriptorRva) ||
            typeDescriptorRva != candidate.TypeDescriptorRva ||
            !view.TryReadUInt32(locatorRva + 16, out uint classHierarchyRva) ||
            classHierarchyRva == 0)
        {
            return false;
        }

        List<RttiBaseClassInfo> baseClasses = [];
        uint baseClassArrayRva = 0;
        if (view.TryReadUInt32(classHierarchyRva + 8, out uint baseClassCount) &&
            view.TryReadUInt32(classHierarchyRva + 12, out baseClassArrayRva) &&
            baseClassArrayRva != 0)
        {
            int count = checked((int)Math.Min(baseClassCount, MaximumBaseClasses));
            for (int index = 0; index < count; index++)
            {
                if (!view.TryReadUInt32(
                        baseClassArrayRva + (uint)(index * 4),
                        out uint baseClassRva) ||
                    baseClassRva == 0 ||
                    !view.TryReadUInt32(
                        baseClassRva,
                        out uint baseTypeDescriptorRva))
                {
                    break;
                }

                string baseName = view.TryReadTypeDescriptorName(
                    baseTypeDescriptorRva,
                    out string decoratedBaseName)
                    ? FriendlyName(decoratedBaseName)
                    : string.Empty;
                view.TryReadUInt32(baseClassRva + 4, out uint containedBases);
                view.TryReadInt32(baseClassRva + 8, out int memberDisplacement);
                view.TryReadInt32(baseClassRva + 12, out int vtableDisplacement);
                view.TryReadInt32(baseClassRva + 16, out int vbtableDisplacement);
                view.TryReadUInt32(baseClassRva + 20, out uint attributes);
                baseClasses.Add(new RttiBaseClassInfo
                {
                    Name = baseName,
                    DecoratedName = decoratedBaseName,
                    TypeDescriptorAddress = module.BaseAddress + baseTypeDescriptorRva,
                    ContainedBaseCount = checked((int)containedBases),
                    MemberDisplacement = memberDisplacement,
                    VtableDisplacement = vtableDisplacement,
                    VbtableDisplacement = vbtableDisplacement,
                    Attributes = attributes
                });
            }
        }

        type = new RttiTypeInfo
        {
            Name = FriendlyName(candidate.DecoratedName),
            DecoratedName = candidate.DecoratedName,
            ModuleName = module.Name,
            ModulePath = module.FilePath,
            Kind = candidate.DecoratedName[3] switch
            {
                'V' => "Class",
                'U' => "Struct",
                'W' => "Enum",
                _ => "Unknown"
            },
            TypeDescriptorAddress = module.BaseAddress + candidate.TypeDescriptorRva,
            CompleteObjectLocatorAddress = module.BaseAddress + locatorRva,
            ClassHierarchyAddress = module.BaseAddress + classHierarchyRva,
            BaseClassArrayAddress = module.BaseAddress + baseClassArrayRva,
            VftableAddress = FindVftable(view, locatorRva, module),
            Signature = signature,
            Offset = offset,
            ConstructorDisplacement = constructorDisplacement,
            BaseClassCount = baseClasses.Count,
            BaseClasses = baseClasses
        };
        return true;
    }

    private static ulong FindVftable(
        PeFileView view,
        uint locatorRva,
        ModuleDescriptor module)
    {
        foreach (PeSectionView section in view.Sections)
        {
            int start = checked((int)section.RawOffset);
            int end = checked((int)Math.Min(
                (ulong)view.Image.Length,
                (ulong)start + section.RawSize));
            if (start < 0 || end <= start || end > view.Image.Length)
            {
                continue;
            }

            for (int offset = start; offset + 4 <= end; offset++)
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(view.Image.AsSpan(offset, 4)) !=
                    locatorRva)
                {
                    continue;
                }

                uint valueRva = view.OffsetToRva(offset);
                if (valueRva >= 8)
                {
                    return module.BaseAddress + valueRva - 8;
                }
            }
        }

        return 0;
    }

    private static string FriendlyName(string decoratedName)
    {
        string name = decoratedName;
        if (name.Length >= 5 && name.StartsWith(".?A", StringComparison.Ordinal))
        {
            name = name[4..];
        }

        int terminator = name.IndexOf("@@", StringComparison.Ordinal);
        if (terminator >= 0)
        {
            name = name[..terminator];
        }

        string[] parts = name.Split(
            '@',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length > 1)
        {
            Array.Reverse(parts);
            return string.Join("::", parts);
        }

        return name.Trim('@');
    }

    private static string ReadAscii(byte[] bytes, int offset, int maximumLength)
    {
        int end = offset;
        int limit = Math.Min(bytes.Length, offset + maximumLength);
        while (end < limit && bytes[end] != 0)
        {
            end++;
        }

        return end > offset
            ? Encoding.ASCII.GetString(bytes, offset, end - offset)
            : string.Empty;
    }

    private sealed class PeFileView
    {
        private PeFileView(
            byte[] image,
            IReadOnlyList<PeSectionView> sections)
        {
            Image = image;
            Sections = sections;
        }

        public byte[] Image { get; }

        public IReadOnlyList<PeSectionView> Sections { get; }

        public static bool TryCreate(byte[] image, out PeFileView view)
        {
            view = null!;
            if (image.Length < 0x100 ||
                image[0] != (byte)'M' ||
                image[1] != (byte)'Z')
            {
                return false;
            }

            int peOffset = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(0x3C, 4));
            if (!IsRangeValid(image, peOffset, 24) ||
                image[peOffset] != (byte)'P' ||
                image[peOffset + 1] != (byte)'E')
            {
                return false;
            }

            int sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(
                image.AsSpan(peOffset + 6, 2));
            int optionalHeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(
                image.AsSpan(peOffset + 20, 2));
            int optionalHeaderOffset = peOffset + 24;
            if (!IsRangeValid(image, optionalHeaderOffset, optionalHeaderSize))
            {
                return false;
            }

            int sectionTableOffset = optionalHeaderOffset + optionalHeaderSize;
            List<PeSectionView> sections = new(sectionCount);
            for (int index = 0; index < sectionCount; index++)
            {
                int offset = sectionTableOffset + index * 40;
                if (!IsRangeValid(image, offset, 40))
                {
                    return false;
                }

                sections.Add(new PeSectionView(
                    BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(offset + 8, 4)),
                    BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(offset + 12, 4)),
                    BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(offset + 16, 4)),
                    BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(offset + 20, 4))));
            }

            view = new PeFileView(image, sections);
            return true;
        }

        public bool TryReadUInt32(uint rva, out uint value)
        {
            value = 0;
            if (!TryRvaToOffset(rva, 4, out int offset))
            {
                return false;
            }

            value = BinaryPrimitives.ReadUInt32LittleEndian(Image.AsSpan(offset, 4));
            return true;
        }

        public bool TryReadInt32(uint rva, out int value)
        {
            value = 0;
            if (!TryRvaToOffset(rva, 4, out int offset))
            {
                return false;
            }

            value = BinaryPrimitives.ReadInt32LittleEndian(Image.AsSpan(offset, 4));
            return true;
        }

        public bool IsRangeValid(uint rva, int length) =>
            TryRvaToOffset(rva, length, out _);

        public uint OffsetToRva(int offset)
        {
            foreach (PeSectionView section in Sections)
            {
                ulong rawStart = section.RawOffset;
                ulong rawEnd = rawStart + section.RawSize;
                if ((ulong)offset >= rawStart && (ulong)offset < rawEnd)
                {
                    return section.VirtualAddress + (uint)(offset - (int)rawStart);
                }
            }

            return checked((uint)offset);
        }

        public bool TryReadTypeDescriptorName(uint typeDescriptorRva, out string name)
        {
            name = string.Empty;
            if (!TryRvaToOffset(typeDescriptorRva + 16, MaximumTypeNameLength, out int offset))
            {
                return false;
            }

            name = ReadAscii(Image, offset, MaximumTypeNameLength);
            return !string.IsNullOrWhiteSpace(name);
        }

        private bool TryRvaToOffset(uint rva, int length, out int offset)
        {
            foreach (PeSectionView section in Sections)
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
                return offset >= 0 && offset + length <= Image.Length;
            }

            if (rva < 0x1000)
            {
                offset = checked((int)rva);
                return offset + length <= Image.Length;
            }

            offset = -1;
            return false;
        }

        private static bool IsRangeValid(byte[] image, int offset, int length) =>
            offset >= 0 && length >= 0 && offset <= image.Length - length;
    }

    private readonly record struct PeSectionView(
        uint VirtualSize,
        uint VirtualAddress,
        uint RawSize,
        uint RawOffset);

    private readonly record struct TypeDescriptorCandidate(
        uint TypeDescriptorRva,
        uint NameRva,
        string DecoratedName);
}
