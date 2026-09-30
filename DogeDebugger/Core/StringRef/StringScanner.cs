using System.Text;
using DogeDebugger.Core.Memory;
using DogeDebugger.Core.Native;
using DogeDebugger.Core.Process;

namespace DogeDebugger.Core.StringRef;

public sealed class StringScanner
{
    private const int ChunkSize = 1024 * 1024;

    private readonly MemoryRegionCatalog _regions;

    public StringScanner(MemoryRegionCatalog regions)
    {
        _regions = regions;
    }

    public IReadOnlyList<StringEntry> Scan(
        ITargetProcess process,
        StringScanOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(options);
        if (options.MinimumLength < 1 ||
            options.MaximumLength < options.MinimumLength ||
            options.MaximumResults < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        List<StringEntry> results = [];
        byte[] buffer = new byte[ChunkSize];
        foreach (MemoryRegionInfo region in _regions.Enumerate(process))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ShouldScan(region, options) || !region.IsReadable)
            {
                continue;
            }

            ulong cursor = region.BaseAddress;
            ulong end = region.BaseAddress + region.Size;
            while (cursor < end)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int requested = checked((int)Math.Min((ulong)ChunkSize, end - cursor));
                int read = process.ReadBytesPartial(cursor, buffer.AsSpan(0, requested));
                if (read <= 0)
                {
                    cursor += (ulong)Math.Max(requested, 1);
                    continue;
                }

                if (options.ScanAsciiUtf8)
                {
                    ScanAscii(buffer.AsSpan(0, read), cursor, options, results, cancellationToken);
                }

                if (options.ScanUtf16Le)
                {
                    ScanUtf16(buffer.AsSpan(0, read), cursor, options, results, cancellationToken);
                }

                if (results.Count >= options.MaximumResults)
                {
                    return results
                        .OrderBy(static entry => entry.Address)
                        .ToArray();
                }

                if (read < requested)
                {
                    break;
                }

                cursor += checked((uint)read);
            }
        }

        return results
            .OrderBy(static entry => entry.Address)
            .ToArray();
    }

    private static void ScanAscii(
        ReadOnlySpan<byte> bytes,
        ulong baseAddress,
        StringScanOptions options,
        List<StringEntry> results,
        CancellationToken cancellationToken)
    {
        int index = 0;
        while (index < bytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsPrintableAscii(bytes[index]))
            {
                index++;
                continue;
            }

            int start = index;
            while (index < bytes.Length &&
                   IsPrintableAscii(bytes[index]) &&
                   index - start < options.MaximumLength)
            {
                index++;
            }

            int length = index - start;
            if (length >= options.MinimumLength)
            {
                results.Add(new StringEntry
                {
                    Address = baseAddress + (ulong)start,
                    ByteLength = length,
                    Text = Encoding.UTF8.GetString(bytes.Slice(start, length)),
                    EncodingName = "UTF8"
                });
            }
        }
    }

    private static void ScanUtf16(
        ReadOnlySpan<byte> bytes,
        ulong baseAddress,
        StringScanOptions options,
        List<StringEntry> results,
        CancellationToken cancellationToken)
    {
        int index = 0;
        while (index + 1 < bytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ushort value = BitConverter.ToUInt16(bytes[index..]);
            if (!IsPrintableUtf16(value))
            {
                index += 2;
                continue;
            }

            int start = index;
            while (index + 1 < bytes.Length &&
                   IsPrintableUtf16(BitConverter.ToUInt16(bytes[index..])) &&
                   index - start < options.MaximumLength * 2)
            {
                index += 2;
            }

            int byteLength = index - start;
            if (byteLength / 2 >= options.MinimumLength)
            {
                results.Add(new StringEntry
                {
                    Address = baseAddress + (ulong)start,
                    ByteLength = byteLength,
                    Text = Encoding.Unicode.GetString(bytes.Slice(start, byteLength)),
                    EncodingName = "UTF-16LE"
                });
            }
        }
    }

    private static bool ShouldScan(MemoryRegionInfo region, StringScanOptions options)
    {
        if (region.State != NativeMethods.MemCommit)
        {
            return false;
        }

        return region.Type switch
        {
            NativeMethods.MemImage => options.IncludeImageMemory,
            NativeMethods.MemPrivate => options.IncludePrivateMemory,
            NativeMethods.MemMapped => options.IncludeMappedMemory,
            _ => false
        };
    }

    private static bool IsPrintableAscii(byte value) => value is >= 0x20 and <= 0x7E;

    private static bool IsPrintableUtf16(ushort value) =>
        value is >= 0x20 and not 0x7F and not 0xFFFE and not 0xFFFF;
}
