using DogeDebugger.Core.Memory;
using DogeDebugger.Core.Modules;
using DogeDebugger.Core.Process;

namespace DogeDebugger.Core.Search;

public sealed class PointerScanner
{
    private const int ChunkSize = 1024 * 1024;

    private readonly MemoryRegionCatalog _regions;

    public PointerScanner(MemoryRegionCatalog regions)
    {
        _regions = regions;
    }

    public PointerScanResult Scan(
        ITargetProcess process,
        IReadOnlyList<ModuleDescriptor> modules,
        PointerScanOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegative(options.MaximumDepth);
        ArgumentOutOfRangeException.ThrowIfNegative(options.MaximumOffset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.Alignment);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumCandidatesPerLevel);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumResults);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumOffsetsPerNode);

        IReadOnlyList<MemoryRegionInfo> regions = _regions
            .Enumerate(process)
            .Where(region => options.IncludeMappedMemory || region.Type != 0x40000)
            .ToArray();
        List<PointerChain> chains = [];
        List<PointerFrontier> frontier =
        [
            new PointerFrontier(options.TargetAddress, [])
        ];
        ulong scannedBytes = 0;
        bool truncated = false;

        for (int depth = 0; depth < options.MaximumDepth && frontier.Count != 0; depth++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Dictionary<ulong, PointerFrontier> next = [];
            foreach (PointerFrontier current in frontier)
            {
                PointerLookupResult lookup = FindPointersTo(
                    process,
                    regions,
                    current.Address,
                    options,
                    cancellationToken);
                scannedBytes += lookup.ScannedBytes;

                IEnumerable<PointerCandidate> candidates = lookup.Candidates;
                if (options.LimitOffsetsPerNode)
                {
                    candidates = candidates
                        .OrderBy(candidate => Math.Abs(
                            unchecked((long)(current.Address - candidate.Value))))
                        .Take(options.MaximumOffsetsPerNode);
                }

                foreach (PointerCandidate candidate in candidates)
                {
                    int offset = unchecked((int)(current.Address - candidate.Value));
                    List<int> offsets = [offset, .. current.Offsets];
                    ModuleDescriptor? module = ModuleCatalog.FindByAddress(
                        modules,
                        candidate.Address);
                    if (module is not null || !options.StaticOnlyBase)
                    {
                        chains.Add(new PointerChain
                        {
                            BaseAddress = candidate.Address,
                            ModuleName = module?.Name ?? string.Empty,
                            ModuleOffset = module is null
                                ? candidate.Address
                                : candidate.Address - module.BaseAddress,
                            Offsets = offsets
                        });

                        if (chains.Count >= options.MaximumResults)
                        {
                            truncated = true;
                            break;
                        }

                        continue;
                    }

                    next.TryAdd(
                        candidate.Address,
                        new PointerFrontier(candidate.Address, offsets));
                    if (next.Count >= options.MaximumCandidatesPerLevel)
                    {
                        truncated = true;
                        break;
                    }
                }

                if (truncated)
                {
                    break;
                }
            }

            frontier = next.Values.ToList();
            if (truncated)
            {
                break;
            }
        }

        return new PointerScanResult
        {
            Truncated = truncated,
            ScannedBytes = scannedBytes,
            Chains = chains
                .OrderBy(static chain => chain.Offsets.Count)
                .ThenBy(static chain => chain.ModuleName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static chain => chain.ModuleOffset)
                .ToArray()
        };
    }

    public PointerScanResult Rescan(
        ITargetProcess process,
        IReadOnlyList<ModuleDescriptor> modules,
        PointerScanResult previous,
        PointerScanRescanOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(options);
        if (options.ValueSize is not (1 or 2 or 4 or 8))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Rescan value size must be 1, 2, 4, or 8 bytes.");
        }

        List<PointerChain> chains = [];
        foreach (PointerChain chain in previous.Chains)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryResolvePointerChain(
                    process,
                    modules,
                    chain,
                    out ulong resolvedAddress))
            {
                continue;
            }

            bool accepted = options.Mode switch
            {
                PointerScanRescanMode.Address =>
                    resolvedAddress == options.Target,
                PointerScanRescanMode.Value =>
                    TryReadUnsignedValue(
                        process,
                        resolvedAddress,
                        options.ValueSize,
                        out ulong value) &&
                    value == options.Target,
                _ => false
            };
            if (accepted)
            {
                chains.Add(chain);
            }
        }

        return new PointerScanResult
        {
            Truncated = previous.Truncated,
            ScannedBytes = previous.ScannedBytes,
            Chains = chains
        };
    }

    private static PointerLookupResult FindPointersTo(
        ITargetProcess process,
        IReadOnlyList<MemoryRegionInfo> regions,
        ulong targetAddress,
        PointerScanOptions options,
        CancellationToken cancellationToken)
    {
        ulong minimum = options.AllowNegativeOffsets &&
                        targetAddress > (ulong)options.MaximumOffset
            ? targetAddress - (ulong)options.MaximumOffset
            : targetAddress;
        ulong maximum = targetAddress + (ulong)options.MaximumOffset;
        int pointerSize = process.Is64Bit ? sizeof(ulong) : sizeof(uint);
        List<PointerCandidate> candidates = [];
        ulong scanned = 0;
        byte[] buffer = new byte[ChunkSize + pointerSize - 1];

        foreach (MemoryRegionInfo region in regions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!region.IsReadable ||
                (options.WritableOnly && !region.IsWritable))
            {
                continue;
            }

            ulong address = AlignUp(region.BaseAddress, options.Alignment);
            ulong end = region.BaseAddress + region.Size;
            while (address + (ulong)pointerSize <= end)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int requested = checked((int)Math.Min(
                    (ulong)ChunkSize,
                    end - address));
                int read = process.ReadBytesPartial(
                    address,
                    buffer.AsSpan(0, requested + pointerSize - 1));
                if (read < pointerSize)
                {
                    break;
                }

                scanned += (ulong)read;
                int lastOffset = read - pointerSize;
                for (int offset = 0; offset <= lastOffset; offset += options.Alignment)
                {
                    ulong value = process.Is64Bit
                        ? BitConverter.ToUInt64(buffer, offset)
                        : BitConverter.ToUInt32(buffer, offset);
                    if (value < minimum || value > maximum)
                    {
                        continue;
                    }

                    candidates.Add(new PointerCandidate(
                        address + (ulong)offset,
                        value));
                    if (candidates.Count >= options.MaximumCandidatesPerLevel)
                    {
                        return new PointerLookupResult(candidates, scanned);
                    }
                }

                if (read < requested)
                {
                    address += (ulong)Math.Max(read, pointerSize);
                }
                else
                {
                    address += (ulong)Math.Max(1, requested - pointerSize + 1);
                }
            }
        }

        return new PointerLookupResult(candidates, scanned);
    }

    private static bool TryResolvePointerChain(
        ITargetProcess process,
        IReadOnlyList<ModuleDescriptor> modules,
        PointerChain chain,
        out ulong finalAddress)
    {
        finalAddress = 0;
        ulong current;
        if (string.IsNullOrWhiteSpace(chain.ModuleName))
        {
            current = chain.BaseAddress;
        }
        else
        {
            ModuleDescriptor? module = modules.FirstOrDefault(candidate =>
                candidate.Name.Equals(
                    chain.ModuleName,
                    StringComparison.OrdinalIgnoreCase));
            if (module is null)
            {
                return false;
            }

            current = module.BaseAddress + chain.ModuleOffset;
        }

        for (int index = chain.Offsets.Count - 1; index >= 0; index--)
        {
            if (!TryReadPointer(process, current, out ulong pointer) ||
                pointer == 0)
            {
                return false;
            }

            current = unchecked(pointer + (ulong)(long)chain.Offsets[index]);
        }

        finalAddress = current;
        return true;
    }

    private static bool TryReadPointer(
        ITargetProcess process,
        ulong address,
        out ulong value)
    {
        int size = process.Is64Bit ? sizeof(ulong) : sizeof(uint);
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        if (!process.TryReadBytes(address, buffer[..size]))
        {
            value = 0;
            return false;
        }

        value = process.Is64Bit
            ? BitConverter.ToUInt64(buffer)
            : BitConverter.ToUInt32(buffer);
        return true;
    }

    private static bool TryReadUnsignedValue(
        ITargetProcess process,
        ulong address,
        int size,
        out ulong value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        if (!process.TryReadBytes(address, buffer[..size]))
        {
            value = 0;
            return false;
        }

        value = size switch
        {
            1 => buffer[0],
            2 => BitConverter.ToUInt16(buffer),
            4 => BitConverter.ToUInt32(buffer),
            8 => BitConverter.ToUInt64(buffer),
            _ => 0
        };
        return true;
    }

    private static ulong AlignUp(ulong value, int alignment)
    {
        ulong mask = (ulong)alignment - 1;
        return (value + mask) & ~mask;
    }

    private sealed record PointerFrontier(
        ulong Address,
        IReadOnlyList<int> Offsets);

    private sealed record PointerCandidate(
        ulong Address,
        ulong Value);

    private sealed record PointerLookupResult(
        IReadOnlyList<PointerCandidate> Candidates,
        ulong ScannedBytes);
}
