using System.Collections.Concurrent;

namespace DogeDebugger.Core.CrossReference;

public sealed class XrefDatabase
{
    private static readonly ConcurrentDictionary<string, XrefDatabase> EmptyCache = [];

    private readonly XrefEntry[] _entries;
    private readonly XrefRecord[] _byTarget;
    private readonly int[] _bySourceIndex;
    private readonly FunctionEntry[] _functions;

    private XrefDatabase(
        string filePath,
        string moduleName,
        ulong imageBase,
        int bitness,
        XrefEntry[] entries,
        XrefRecord[] byTarget,
        int[] bySourceIndex,
        FunctionEntry[] functions,
        TimeSpan analysisDuration)
    {
        FilePath = filePath;
        ModuleName = moduleName;
        ImageBase = imageBase;
        Bitness = bitness;
        AnalysisDuration = analysisDuration;
        _entries = entries;
        _byTarget = byTarget;
        _bySourceIndex = bySourceIndex;
        _functions = functions;
    }

    public string FilePath { get; }

    public string ModuleName { get; }

    public ulong ImageBase { get; }

    public int Bitness { get; }

    public int TotalXrefs => _byTarget.Length;

    public int TotalFunctions => _functions.Length;

    public TimeSpan AnalysisDuration { get; }

    public IReadOnlyList<XrefEntry> Entries => _entries;

    public ReadOnlySpan<FunctionEntry> Functions => _functions;

    public ReadOnlySpan<XrefRecord> GetXrefsTo(uint targetRva)
    {
        (int start, int count) = FindTargetRange(targetRva);
        return count == 0 ? default : _byTarget.AsSpan(start, count);
    }

    public int CountXrefsTo(uint targetRva) => FindTargetRange(targetRva).Count;

    public FunctionEntry? FindFunctionAt(uint startRva)
    {
        int index = Array.BinarySearch(
            _functions,
            new FunctionEntry(startRva, 0, null, FunctionSource.Pdata),
            FunctionStartComparer.Instance);
        return index < 0 ? null : _functions[index];
    }

    public FunctionEntry? FindFunctionContaining(uint rva)
    {
        int index = FindContainingFunctionIndex(rva);
        return index < 0 ? null : _functions[index];
    }

    public FunctionEntry? FindFunctionByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        foreach (FunctionEntry function in _functions)
        {
            if (string.Equals(function.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return function;
            }
        }

        return null;
    }

    public ulong RvaToVa(uint rva) => ImageBase + rva;

    public uint? VaToRva(ulong va)
    {
        if (ImageBase == 0 || va < ImageBase)
        {
            return null;
        }

        ulong rva = va - ImageBase;
        return rva <= uint.MaxValue ? (uint)rva : null;
    }

    public IReadOnlyList<XrefEntry> Find(ulong targetAddress)
    {
        if (ImageBase != 0 && VaToRva(targetAddress) is { } targetRva)
        {
            ReadOnlySpan<XrefRecord> records = GetXrefsTo(targetRva);
            if (records.IsEmpty)
            {
                return [];
            }

            List<XrefEntry> matches = new(records.Length);
            foreach (XrefRecord record in records)
            {
                matches.Add(ToEntry(record));
            }

            return matches;
        }

        return _entries
            .Where(entry => entry.ToAddress == targetAddress)
            .ToArray();
    }

    public static XrefDatabase FromEntries(
        IReadOnlyList<XrefEntry> entries,
        string moduleName = "",
        string filePath = "",
        ulong imageBase = 0,
        int bitness = 0,
        TimeSpan analysisDuration = default)
    {
        XrefEntry[] materialized = entries.ToArray();
        return new XrefDatabase(
            filePath,
            moduleName,
            imageBase,
            bitness,
            materialized,
            [],
            [],
            [],
            analysisDuration);
    }

    internal static XrefDatabase CreateModuleDatabase(
        string filePath,
        string moduleName,
        ulong imageBase,
        int bitness,
        XrefRecord[] byTarget,
        int[] bySourceIndex,
        FunctionEntry[] functions,
        TimeSpan analysisDuration)
    {
        XrefEntry[] entries = new XrefEntry[byTarget.Length];
        for (int index = 0; index < byTarget.Length; index++)
        {
            entries[index] = CreateEntry(byTarget[index], imageBase, moduleName);
        }

        return new XrefDatabase(
            filePath,
            moduleName,
            imageBase,
            bitness,
            entries,
            byTarget,
            bySourceIndex,
            functions,
            analysisDuration);
    }

    internal static XrefDatabase Empty(string moduleName = "") =>
        EmptyCache.GetOrAdd(
            moduleName,
            static name => new XrefDatabase(
                string.Empty,
                name,
                0,
                0,
                [],
                [],
                [],
                [],
                TimeSpan.Zero));

    private XrefEntry ToEntry(XrefRecord record) =>
        CreateEntry(record, ImageBase, ModuleName);

    private static XrefEntry CreateEntry(
        XrefRecord record,
        ulong imageBase,
        string moduleName) =>
        new()
        {
            FromAddress = imageBase + record.SourceRva,
            ToAddress = imageBase + record.TargetRva,
            Kind = record.Kind,
            ModuleName = moduleName,
            InstructionText = string.Empty
        };

    private int FindContainingFunctionIndex(uint rva)
    {
        int low = 0;
        int high = _functions.Length - 1;
        int candidate = -1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            if (_functions[middle].StartRva <= rva)
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
            FunctionEntry function = _functions[candidate];
            if (function.EndRva > function.StartRva && rva < function.EndRva)
            {
                return candidate;
            }
        }

        return -1;
    }

    private (int Start, int Count) FindTargetRange(uint targetRva)
    {
        int start = LowerBoundByTarget(targetRva);
        if (start >= _byTarget.Length || _byTarget[start].TargetRva != targetRva)
        {
            return (0, 0);
        }

        int end = start;
        while (end < _byTarget.Length && _byTarget[end].TargetRva == targetRva)
        {
            end++;
        }

        return (start, end - start);
    }

    private int LowerBoundByTarget(uint targetRva)
    {
        int low = 0;
        int high = _byTarget.Length;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (_byTarget[middle].TargetRva < targetRva)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    private sealed class FunctionStartComparer : IComparer<FunctionEntry>
    {
        public static readonly FunctionStartComparer Instance = new();

        public int Compare(FunctionEntry x, FunctionEntry y) =>
            x.StartRva.CompareTo(y.StartRva);
    }
}
