using System.Globalization;
using System.Text;
using DogeDebugger.Core.Memory;
using DogeDebugger.Core.Native;
using DogeDebugger.Core.Process;

namespace DogeDebugger.Core.Search;

public sealed class MemoryValueScanner
{
    private const int ChunkSize = 1024 * 1024;

    private static readonly MemoryValueKind[] AllNumericKinds =
    [
        MemoryValueKind.Byte,
        MemoryValueKind.Int16,
        MemoryValueKind.Int32,
        MemoryValueKind.Int64,
        MemoryValueKind.Single,
        MemoryValueKind.Double
    ];

    private readonly MemoryRegionCatalog _regions;

    public MemoryValueScanner(MemoryRegionCatalog regions)
    {
        _regions = regions;
    }

    public MemoryValueScanResult InitialScan(
        ITargetProcess process,
        MemoryValueScanOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);

        if (options.Kind == MemoryValueKind.AllTypes)
        {
            return InitialAllTypesScan(process, options, cancellationToken);
        }

        if (options.Kind == MemoryValueKind.ByteArray)
        {
            return InitialAobScan(process, options, cancellationToken);
        }

        MemoryValueSample? expected = null;
        if (RequiresValue(options.Comparison))
        {
            if (!MemoryValueSample.TryParse(
                    options.Value,
                    options.Kind,
                    options.IgnoreCase,
                    options.TextEncoding,
                    out expected) ||
                expected is null)
            {
                throw new FormatException($"Could not parse '{options.Value}' as {options.Kind}.");
            }
        }

        MemoryValueSample? second = null;
        if (options.Comparison == MemoryValueComparison.Between)
        {
            if (string.IsNullOrEmpty(options.SecondValue) ||
                !MemoryValueSample.TryParse(
                    options.SecondValue,
                    options.Kind,
                    options.IgnoreCase,
                    options.TextEncoding,
                    out second) ||
                second is null)
            {
                throw new FormatException("A valid second value is required for a between scan.");
            }
        }

        int valueSize = expected?.Size ?? MemoryValueSample.SizeOf(options.Kind);
        if (valueSize <= 0)
        {
            throw new NotSupportedException($"The value type {options.Kind} is not supported for numeric scans.");
        }

        List<MemoryValueMatch> matches = [];
        ulong scanned = 0;
        bool truncated = false;
        byte[] buffer = new byte[ChunkSize + Math.Max(0, valueSize - 1)];

        foreach (MemoryRegionInfo region in _regions.Enumerate(process))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ShouldScan(region, options) || !region.IsReadable)
            {
                continue;
            }

            ulong start = AlignUp(
                Math.Max(region.BaseAddress, options.StartAddress),
                options.Alignment);
            ulong end = Math.Min(region.BaseAddress + region.Size, options.EndAddress);
            if (end <= start)
            {
                continue;
            }

            ulong cursor = start;
            while (cursor < end)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int requested = checked((int)Math.Min((ulong)ChunkSize, end - cursor));
                int read = process.ReadBytesPartial(cursor, buffer.AsSpan(0, requested + valueSize - 1));
                if (read < valueSize)
                {
                    cursor += (ulong)Math.Max(requested, 1);
                    continue;
                }

                scanned += (ulong)read;
                int lastOffset = Math.Max(0, read - valueSize);
                for (int offset = 0; offset <= lastOffset; offset += options.Alignment)
                {
                    ReadOnlySpan<byte> candidateBytes = buffer.AsSpan(offset, valueSize);
                    if (!MemoryValueSample.TryCreate(
                            options.Kind,
                            candidateBytes,
                            options.IgnoreCase,
                            options.TextEncoding,
                            out MemoryValueSample? candidate) ||
                        candidate is null ||
                        !MatchesInitial(candidate, expected, second, options.Comparison))
                    {
                        continue;
                    }

                    byte[] currentBytes = candidate.Bytes;
                    matches.Add(new MemoryValueMatch
                    {
                        Address = cursor + (ulong)offset,
                        Kind = options.Kind,
                        PreviousBytes = currentBytes,
                        CurrentBytes = currentBytes,
                        PreviousValueText = FormatValue(
                            currentBytes,
                            options.Kind,
                            options.IgnoreCase,
                            options.TextEncoding),
                        DisplayValue = FormatValue(
                            currentBytes,
                            options.Kind,
                            options.IgnoreCase,
                            options.TextEncoding)
                    });

                    if (matches.Count >= options.MaximumResults)
                    {
                        truncated = true;
                        return new MemoryValueScanResult
                        {
                            Truncated = true,
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
                    cursor += (ulong)Math.Max(1, requested - valueSize + 1);
                }
            }
        }

        return new MemoryValueScanResult
        {
            Truncated = truncated,
            ScannedBytes = scanned,
            Matches = matches
        };
    }

    public MemoryValueScanResult NextScan(
        ITargetProcess process,
        IReadOnlyList<MemoryValueMatch> previousMatches,
        MemoryValueScanOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(previousMatches);
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);

        if (options.Kind == MemoryValueKind.AllTypes)
        {
            return NextAllTypesScan(
                process,
                previousMatches,
                options,
                cancellationToken);
        }

        if (options.Kind == MemoryValueKind.ByteArray)
        {
            return NextAobScan(process, previousMatches, options, cancellationToken);
        }

        MemoryValueSample? expected = null;
        if (RequiresValue(options.Comparison))
        {
            if (!MemoryValueSample.TryParse(
                    options.Value,
                    options.Kind,
                    options.IgnoreCase,
                    options.TextEncoding,
                    out expected) ||
                expected is null)
            {
                throw new FormatException($"Could not parse '{options.Value}' as {options.Kind}.");
            }
        }

        MemoryValueSample? second = null;
        if (options.Comparison == MemoryValueComparison.Between)
        {
            if (string.IsNullOrEmpty(options.SecondValue) ||
                !MemoryValueSample.TryParse(
                    options.SecondValue,
                    options.Kind,
                    options.IgnoreCase,
                    options.TextEncoding,
                    out second) ||
                second is null)
            {
                throw new FormatException("A valid second value is required for a between scan.");
            }
        }

        List<MemoryValueMatch> matches = [];
        int valueSize = expected?.Size ?? MemoryValueSample.SizeOf(options.Kind);
        if (valueSize <= 0)
        {
            throw new NotSupportedException($"The value type {options.Kind} is not supported for numeric scans.");
        }

        foreach (MemoryValueMatch previous in previousMatches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!MemoryValueSample.TryCreate(
                    options.Kind,
                    previous.CurrentBytes,
                    options.IgnoreCase,
                    options.TextEncoding,
                    out MemoryValueSample? previousValue) ||
                previousValue is null)
            {
                continue;
            }

            byte[] currentBytes = process.ReadBytes(previous.Address, valueSize);
            if (currentBytes.Length != valueSize ||
                !MemoryValueSample.TryCreate(
                    options.Kind,
                    currentBytes,
                    options.IgnoreCase,
                    options.TextEncoding,
                    out MemoryValueSample? currentValue) ||
                currentValue is null)
            {
                continue;
            }

            bool matched = options.Comparison switch
            {
                MemoryValueComparison.UnknownInitialValue => true,
                MemoryValueComparison.Changed => !previousValue.Matches(
                    currentValue,
                    MemoryValueComparison.Unchanged,
                    second),
                MemoryValueComparison.Unchanged => previousValue.Matches(
                    currentValue,
                    MemoryValueComparison.Unchanged,
                    second),
                MemoryValueComparison.Increased => currentValue.Matches(
                    previousValue,
                    MemoryValueComparison.GreaterThan,
                    second),
                MemoryValueComparison.Decreased => currentValue.Matches(
                    previousValue,
                    MemoryValueComparison.LessThan,
                    second),
                MemoryValueComparison.IncreasedBy => expected!.MatchesDifference(
                    previousValue,
                    currentValue,
                    decreasing: false),
                MemoryValueComparison.DecreasedBy => expected!.MatchesDifference(
                    previousValue,
                    currentValue,
                    decreasing: true),
                _ => expected!.Matches(currentValue, options.Comparison, second)
            };

            if (!matched)
            {
                continue;
            }

            matches.Add(new MemoryValueMatch
            {
                Address = previous.Address,
                Kind = options.Kind,
                PreviousBytes = previous.CurrentBytes,
                CurrentBytes = currentBytes,
                PreviousValueText = FormatValue(
                    previous.CurrentBytes,
                    options.Kind,
                    options.IgnoreCase,
                    options.TextEncoding),
                DisplayValue = FormatValue(
                    currentBytes,
                    options.Kind,
                    options.IgnoreCase,
                    options.TextEncoding)
            });
        }

        return new MemoryValueScanResult
        {
            ScannedBytes = (ulong)(previousMatches.Count * Math.Max(valueSize, 1)),
            Matches = matches
        };
    }

    private static bool MatchesInitial(
        MemoryValueSample candidate,
        MemoryValueSample? expected,
        MemoryValueSample? second,
        MemoryValueComparison comparison)
    {
        return comparison switch
        {
            MemoryValueComparison.UnknownInitialValue or
            MemoryValueComparison.Changed or
            MemoryValueComparison.Unchanged or
            MemoryValueComparison.Increased or
            MemoryValueComparison.Decreased or
            MemoryValueComparison.IncreasedBy or
            MemoryValueComparison.DecreasedBy => true,
            _ => expected!.Matches(candidate, comparison, second)
        };
    }

    private static bool RequiresValue(MemoryValueComparison comparison)
    {
        return comparison switch
        {
            MemoryValueComparison.UnknownInitialValue or
            MemoryValueComparison.Changed or
            MemoryValueComparison.Unchanged or
            MemoryValueComparison.Increased or
            MemoryValueComparison.Decreased => false,
            _ => true
        };
    }

    private static bool ShouldScan(MemoryRegionInfo region, MemoryValueScanOptions options)
    {
        if (region.State != NativeMethods.MemCommit)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(options.ModuleName) &&
            !string.Equals(
                region.ModuleName,
                options.ModuleName,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (options.RequireWritable is bool requireWritable &&
            region.IsWritable != requireWritable)
        {
            return false;
        }

        if (options.RequireExecutable is bool requireExecutable &&
            region.IsExecutable != requireExecutable)
        {
            return false;
        }

        if (options.RequireCopyOnWrite is bool requireCopyOnWrite &&
            region.IsCopyOnWrite != requireCopyOnWrite)
        {
            return false;
        }

        if (options.WritableOnly && !region.IsWritable)
        {
            return false;
        }

        if (options.ExecutableOnly && !region.IsExecutable)
        {
            return false;
        }

        return region.Type switch
        {
            NativeMethods.MemPrivate => options.SearchPrivateMemory,
            NativeMethods.MemImage => options.SearchImageMemory,
            NativeMethods.MemMapped => options.SearchMappedMemory,
            _ => false
        };
    }

    private static void ValidateOptions(MemoryValueScanOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.Alignment);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumResults);
        if (options.Alignment is not (1 or 2 or 4 or 8 or 16))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Alignment must be 1, 2, 4, 8, or 16.");
        }
    }

    private static ulong AlignUp(ulong value, int alignment)
    {
        ulong mask = (ulong)alignment - 1;
        return (value + mask) & ~mask;
    }

    private static string FormatValue(
        ReadOnlySpan<byte> bytes,
        MemoryValueKind kind,
        bool ignoreCase,
        Encoding textEncoding)
    {
        return kind switch
        {
            MemoryValueKind.Byte => bytes[0].ToString(CultureInfo.InvariantCulture),
            MemoryValueKind.SByte => unchecked((sbyte)bytes[0]).ToString(CultureInfo.InvariantCulture),
            MemoryValueKind.Int16 => BitConverter.ToInt16(bytes).ToString(CultureInfo.InvariantCulture),
            MemoryValueKind.UInt16 => BitConverter.ToUInt16(bytes).ToString(CultureInfo.InvariantCulture),
            MemoryValueKind.Int32 => BitConverter.ToInt32(bytes).ToString(CultureInfo.InvariantCulture),
            MemoryValueKind.UInt32 => BitConverter.ToUInt32(bytes).ToString(CultureInfo.InvariantCulture),
            MemoryValueKind.Int64 => BitConverter.ToInt64(bytes).ToString(CultureInfo.InvariantCulture),
            MemoryValueKind.UInt64 => BitConverter.ToUInt64(bytes).ToString(CultureInfo.InvariantCulture),
            MemoryValueKind.Single => BitConverter.ToSingle(bytes).ToString("R", CultureInfo.InvariantCulture),
            MemoryValueKind.Double => BitConverter.ToDouble(bytes).ToString("R", CultureInfo.InvariantCulture),
            MemoryValueKind.Utf8String => textEncoding.GetString(bytes),
            MemoryValueKind.Utf16String => textEncoding.GetString(bytes),
            _ => Convert.ToHexString(bytes)
        };
    }

    private MemoryValueScanResult InitialAobScan(
        ITargetProcess process,
        MemoryValueScanOptions options,
        CancellationToken cancellationToken)
    {
        if (!BytePattern.TryParse(
                options.Value,
                out BytePattern? pattern,
                out string error) ||
            pattern is null)
        {
            throw new FormatException(error);
        }

        List<MemoryValueMatch> matches = [];
        ulong scanned = 0;
        byte[] buffer = new byte[ChunkSize + pattern.Length - 1];
        foreach (MemoryRegionInfo region in _regions.Enumerate(process))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ShouldScan(region, options) || !region.IsReadable)
            {
                continue;
            }

            ulong start = Math.Max(region.BaseAddress, options.StartAddress);
            ulong end = Math.Min(region.BaseAddress + region.Size, options.EndAddress);
            if (end <= start)
            {
                continue;
            }

            ulong cursor = start;
            while (cursor < end)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int requested = checked((int)Math.Min((ulong)ChunkSize, end - cursor));
                int read = process.ReadBytesPartial(
                    cursor,
                    buffer.AsSpan(0, requested + pattern.Length - 1));
                if (read < pattern.Length)
                {
                    cursor += (ulong)Math.Max(requested, 1);
                    continue;
                }

                scanned += (ulong)read;
                int lastOffset = read - pattern.Length;
                for (int offset = 0; offset <= lastOffset; offset++)
                {
                    if (!pattern.Matches(buffer.AsSpan(offset, pattern.Length)))
                    {
                        continue;
                    }

                    byte[] currentBytes = buffer
                        .AsSpan(offset, pattern.Length)
                        .ToArray();
                    matches.Add(new MemoryValueMatch
                    {
                        Address = cursor + (ulong)offset,
                        Kind = MemoryValueKind.ByteArray,
                        PreviousBytes = currentBytes,
                        CurrentBytes = currentBytes,
                        PreviousValueText = pattern.DisplayText,
                        DisplayValue = pattern.DisplayText
                    });
                    if (matches.Count >= options.MaximumResults)
                    {
                        return new MemoryValueScanResult
                        {
                            Truncated = true,
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
                    cursor += (ulong)Math.Max(
                        1,
                        requested - pattern.Length + 1);
                }
            }
        }

        return new MemoryValueScanResult
        {
            ScannedBytes = scanned,
            Matches = matches
        };
    }

    private static MemoryValueScanResult NextAobScan(
        ITargetProcess process,
        IReadOnlyList<MemoryValueMatch> previousMatches,
        MemoryValueScanOptions options,
        CancellationToken cancellationToken)
    {
        if (!BytePattern.TryParse(
                options.Value,
                out BytePattern? pattern,
                out string error) ||
            pattern is null)
        {
            throw new FormatException(error);
        }

        List<MemoryValueMatch> matches = [];
        foreach (MemoryValueMatch previous in previousMatches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] currentBytes = process.ReadBytes(
                previous.Address,
                pattern.Length);
            if (currentBytes.Length != pattern.Length ||
                !pattern.Matches(currentBytes))
            {
                continue;
            }

            matches.Add(new MemoryValueMatch
            {
                Address = previous.Address,
                Kind = MemoryValueKind.ByteArray,
                PreviousBytes = previous.CurrentBytes,
                CurrentBytes = currentBytes,
                PreviousValueText = pattern.DisplayText,
                DisplayValue = pattern.DisplayText
            });
        }

        return new MemoryValueScanResult
        {
            ScannedBytes = (ulong)(previousMatches.Count * pattern.Length),
            Matches = matches
        };
    }

    private MemoryValueScanResult InitialAllTypesScan(
        ITargetProcess process,
        MemoryValueScanOptions options,
        CancellationToken cancellationToken)
    {
        Dictionary<MemoryValueKind, MemoryValueSample?> expected = [];
        Dictionary<MemoryValueKind, MemoryValueSample?> second = [];
        foreach (MemoryValueKind kind in AllNumericKinds)
        {
            if (RequiresValue(options.Comparison))
            {
                if (!MemoryValueSample.TryParse(
                        options.Value,
                        kind,
                        options.IgnoreCase,
                        options.TextEncoding,
                        out MemoryValueSample? expectedSample) ||
                    expectedSample is null)
                {
                    expected[kind] = null;
                }
                else
                {
                    expected[kind] = expectedSample;
                }
            }

            if (options.Comparison == MemoryValueComparison.Between)
            {
                if (!MemoryValueSample.TryParse(
                        options.SecondValue ?? string.Empty,
                        kind,
                        options.IgnoreCase,
                        options.TextEncoding,
                        out MemoryValueSample? secondSample) ||
                    secondSample is null)
                {
                    second[kind] = null;
                }
                else
                {
                    second[kind] = secondSample;
                }
            }
        }

        if (RequiresValue(options.Comparison) &&
            expected.Values.All(static value => value is null))
        {
            throw new FormatException(
                $"Could not parse '{options.Value}' as any supported value type.");
        }

        List<MemoryValueMatch> matches = [];
        ulong scanned = 0;
        const int maximumValueSize = sizeof(double);
        byte[] buffer = new byte[ChunkSize + maximumValueSize - 1];

        foreach (MemoryRegionInfo region in _regions.Enumerate(process))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ShouldScan(region, options) || !region.IsReadable)
            {
                continue;
            }

            ulong start = AlignUp(
                Math.Max(region.BaseAddress, options.StartAddress),
                options.Alignment);
            ulong end = Math.Min(region.BaseAddress + region.Size, options.EndAddress);
            if (end <= start)
            {
                continue;
            }

            ulong cursor = start;
            while (cursor < end)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int requested = checked((int)Math.Min((ulong)ChunkSize, end - cursor));
                int read = process.ReadBytesPartial(
                    cursor,
                    buffer.AsSpan(0, requested + maximumValueSize - 1));
                if (read < 1)
                {
                    cursor += (ulong)Math.Max(requested, 1);
                    continue;
                }

                scanned += (ulong)read;
                int lastOffset = Math.Max(0, read - 1);
                for (int offset = 0; offset <= lastOffset; offset += options.Alignment)
                {
                    foreach (MemoryValueKind kind in AllNumericKinds)
                    {
                        int valueSize = MemoryValueSample.SizeOf(kind);
                        MemoryValueSample? expectedSample =
                            expected.GetValueOrDefault(kind);
                        MemoryValueSample? secondSample =
                            second.GetValueOrDefault(kind);
                        if ((RequiresValue(options.Comparison) &&
                             expectedSample is null) ||
                            (options.Comparison == MemoryValueComparison.Between &&
                             secondSample is null))
                        {
                            continue;
                        }

                        if (offset + valueSize > read ||
                            !MemoryValueSample.TryCreate(
                                kind,
                                buffer.AsSpan(offset, valueSize),
                                options.IgnoreCase,
                                options.TextEncoding,
                                out MemoryValueSample? candidate) ||
                            candidate is null ||
                            !MatchesInitial(
                                candidate,
                                expectedSample,
                                secondSample,
                                options.Comparison))
                        {
                            continue;
                        }

                        byte[] currentBytes = candidate.Bytes;
                        matches.Add(new MemoryValueMatch
                        {
                            Address = cursor + (ulong)offset,
                            Kind = kind,
                            PreviousBytes = currentBytes,
                            CurrentBytes = currentBytes,
                            PreviousValueText = FormatValue(
                                currentBytes,
                                kind,
                                options.IgnoreCase,
                                options.TextEncoding),
                            DisplayValue = FormatValue(
                                currentBytes,
                                kind,
                                options.IgnoreCase,
                                options.TextEncoding)
                        });

                        if (matches.Count >= options.MaximumResults)
                        {
                            return new MemoryValueScanResult
                            {
                                Truncated = true,
                                ScannedBytes = scanned,
                                Matches = matches
                            };
                        }
                    }
                }

                if (read < requested)
                {
                    cursor += (ulong)Math.Max(read, 1);
                }
                else
                {
                    cursor += (ulong)Math.Max(1, requested - maximumValueSize + 1);
                }
            }
        }

        return new MemoryValueScanResult
        {
            ScannedBytes = scanned,
            Matches = matches
        };
    }

    private static MemoryValueScanResult NextAllTypesScan(
        ITargetProcess process,
        IReadOnlyList<MemoryValueMatch> previousMatches,
        MemoryValueScanOptions options,
        CancellationToken cancellationToken)
    {
        List<MemoryValueMatch> matches = [];
        ulong scanned = 0;

        foreach (MemoryValueMatch previous in previousMatches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MemoryValueKind kind = previous.Kind == MemoryValueKind.AllTypes
                ? MemoryValueKind.Int32
                : previous.Kind;
            int valueSize = MemoryValueSample.SizeOf(kind);
            if (valueSize <= 0)
            {
                continue;
            }

            if (!MemoryValueSample.TryCreate(
                    kind,
                    previous.CurrentBytes,
                    options.IgnoreCase,
                    options.TextEncoding,
                    out MemoryValueSample? previousValue) ||
                previousValue is null)
            {
                continue;
            }

            MemoryValueSample? expected = null;
            if (RequiresValue(options.Comparison) &&
                (!MemoryValueSample.TryParse(
                    options.Value,
                    kind,
                    options.IgnoreCase,
                    options.TextEncoding,
                    out expected) ||
                 expected is null))
            {
                continue;
            }

            MemoryValueSample? second = null;
            if (options.Comparison == MemoryValueComparison.Between &&
                (!MemoryValueSample.TryParse(
                    options.SecondValue ?? string.Empty,
                    kind,
                    options.IgnoreCase,
                    options.TextEncoding,
                    out second) ||
                 second is null))
            {
                continue;
            }

            byte[] currentBytes = process.ReadBytes(previous.Address, valueSize);
            scanned += (ulong)Math.Max(valueSize, 1);
            if (currentBytes.Length != valueSize ||
                !MemoryValueSample.TryCreate(
                    kind,
                    currentBytes,
                    options.IgnoreCase,
                    options.TextEncoding,
                    out MemoryValueSample? currentValue) ||
                currentValue is null)
            {
                continue;
            }

            bool matched = options.Comparison switch
            {
                MemoryValueComparison.UnknownInitialValue => true,
                MemoryValueComparison.Changed => !previousValue.Matches(
                    currentValue,
                    MemoryValueComparison.Unchanged,
                    second),
                MemoryValueComparison.Unchanged => previousValue.Matches(
                    currentValue,
                    MemoryValueComparison.Unchanged,
                    second),
                MemoryValueComparison.Increased => currentValue.Matches(
                    previousValue,
                    MemoryValueComparison.GreaterThan,
                    second),
                MemoryValueComparison.Decreased => currentValue.Matches(
                    previousValue,
                    MemoryValueComparison.LessThan,
                    second),
                MemoryValueComparison.IncreasedBy => expected!.MatchesDifference(
                    previousValue,
                    currentValue,
                    decreasing: false),
                MemoryValueComparison.DecreasedBy => expected!.MatchesDifference(
                    previousValue,
                    currentValue,
                    decreasing: true),
                _ => expected!.Matches(currentValue, options.Comparison, second)
            };
            if (!matched)
            {
                continue;
            }

            matches.Add(new MemoryValueMatch
            {
                Address = previous.Address,
                Kind = kind,
                PreviousBytes = previous.CurrentBytes,
                CurrentBytes = currentBytes,
                PreviousValueText = FormatValue(
                    previous.CurrentBytes,
                    kind,
                    options.IgnoreCase,
                    options.TextEncoding),
                DisplayValue = FormatValue(
                    currentBytes,
                    kind,
                    options.IgnoreCase,
                    options.TextEncoding)
            });
        }

        return new MemoryValueScanResult
        {
            ScannedBytes = scanned,
            Matches = matches
        };
    }
}
