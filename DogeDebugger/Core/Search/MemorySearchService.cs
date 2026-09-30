using DogeDebugger.Core.Memory;
using DogeDebugger.Core.Native;
using DogeDebugger.Core.Process;

namespace DogeDebugger.Core.Search;

public sealed class MemorySearchService
{
    private const int ChunkSize = 1024 * 1024;

    private readonly MemoryRegionCatalog _regions;

    public MemorySearchService(MemoryRegionCatalog regions)
    {
        _regions = regions;
    }

    public SearchResult Search(
        ITargetProcess process,
        SearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.MaximumResults);

        if (!BytePattern.TryParse(request.Pattern, out BytePattern? pattern, out string error) ||
            pattern is null)
        {
            throw new FormatException(error);
        }

        List<SearchMatch> matches = [];
        ulong scanned = 0;
        bool truncated = false;
        int overlap = Math.Max(0, pattern.Length - 1);
        byte[] buffer = new byte[ChunkSize + overlap];

        foreach (MemoryRegionInfo region in _regions.Enumerate(process))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ShouldSearch(region, request) || !region.IsReadable)
            {
                continue;
            }

            ulong regionStart = Math.Max(region.BaseAddress, request.StartAddress);
            ulong regionEnd = Math.Min(region.BaseAddress + region.Size, request.EndAddress);
            if (regionEnd <= regionStart)
            {
                continue;
            }

            ulong cursor = regionStart;
            while (cursor < regionEnd)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int requested = checked((int)Math.Min((ulong)ChunkSize, regionEnd - cursor));
                Span<byte> readBuffer = buffer.AsSpan(0, requested + overlap);
                int read = process.ReadBytesPartial(cursor, readBuffer);
                if (read <= 0)
                {
                    cursor += (ulong)requested;
                    continue;
                }

                scanned += (ulong)read;
                int searchableLength = Math.Max(0, read - pattern.Length + 1);
                Span<byte> searchable = readBuffer[..searchableLength];
                for (int index = 0; index < searchable.Length; index++)
                {
                    if (!pattern.Matches(searchable[index..]))
                    {
                        continue;
                    }

                    matches.Add(new SearchMatch
                    {
                        Address = cursor + (ulong)index,
                        Bytes = pattern.Bytes.ToArray()
                    });

                    if (matches.Count >= request.MaximumResults)
                    {
                        truncated = true;
                        return new SearchResult
                        {
                            Truncated = truncated,
                            ScannedBytes = scanned,
                            Matches = matches
                        };
                    }
                }

                if (read < requested)
                {
                    cursor += (ulong)Math.Max(read, 1);
                }
                else
                {
                    cursor += (ulong)(requested - overlap);
                }
            }
        }

        return new SearchResult
        {
            Truncated = truncated,
            ScannedBytes = scanned,
            Matches = matches
        };
    }

    private static bool ShouldSearch(MemoryRegionInfo region, SearchRequest request)
    {
        return region.State switch
        {
            NativeMethods.MemCommit when region.Type == NativeMethods.MemPrivate =>
                request.SearchPrivateMemory,
            NativeMethods.MemCommit when region.Type == NativeMethods.MemImage =>
                request.SearchImageMemory,
            NativeMethods.MemCommit when region.Type == NativeMethods.MemMapped =>
                request.SearchMappedMemory,
            _ => false
        };
    }
}
