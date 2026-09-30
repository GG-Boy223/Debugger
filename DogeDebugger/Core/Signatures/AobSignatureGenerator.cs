using DogeDebugger.Core.Disassembly;
using DogeDebugger.Core.Modules;
using DogeDebugger.Core.Process;
using DogeDebugger.Core.Search;

namespace DogeDebugger.Core.Signatures;

public sealed class AobSignatureGenerator
{
    private readonly DisassemblerService _disassembler = new();
    private readonly MemorySearchService _search;

    public AobSignatureGenerator(MemorySearchService search)
    {
        _search = search;
    }

    public AobSignature? Generate(
        ITargetProcess process,
        ModuleDescriptor? module,
        ulong address,
        AobSignatureOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new AobSignatureOptions();
        if (options.MaximumLength < 1 ||
            options.InstructionCount < 1 ||
            !process.IsOpen)
        {
            return null;
        }

        List<byte> bytes = [];
        List<byte> mask = [];
        ulong current = address;
        Span<byte> instructionBuffer = stackalloc byte[16];

        for (int index = 0;
             index < options.InstructionCount && bytes.Count < options.MaximumLength;
             index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!process.TryReadBytes(current, instructionBuffer))
            {
                break;
            }

            InstructionSnapshot? instruction = _disassembler.DisassembleOne(
                instructionBuffer,
                current,
                process.Is64Bit);
            if (instruction is null || instruction.Length <= 0)
            {
                break;
            }

            int remaining = options.MaximumLength - bytes.Count;
            int take = Math.Min(instruction.Length, remaining);
            bytes.AddRange(instruction.Bytes.AsSpan(0, take).ToArray());
            if (instruction.FixedByteMask.Count == instruction.Length)
            {
                mask.AddRange(instruction.FixedByteMask.Take(take));
            }
            else
            {
                mask.AddRange(Enumerable.Repeat((byte)1, take));
            }

            current += checked((uint)instruction.Length);
        }

        if (bytes.Count == 0)
        {
            return null;
        }

        int length = bytes.Count;
        int matches = 0;
        while (length > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BytePattern pattern = BytePattern.FromMask(
                bytes.Take(length).ToArray(),
                mask.Take(length).ToArray());
            SearchResult result = _search.Search(
                process,
                new SearchRequest
                {
                    Pattern = pattern.DisplayText,
                    StartAddress = module?.BaseAddress ?? 0,
                    EndAddress = module is null ? ulong.MaxValue : module.BaseAddress + module.Size,
                    MaximumResults = 2,
                    SearchPrivateMemory = module is null,
                    SearchImageMemory = module is not null,
                    SearchMappedMemory = false
                },
                cancellationToken);
            matches = result.Matches.Count;
            if (!options.RequireUniqueResult || matches == 1)
            {
                return new AobSignature
                {
                    Pattern = pattern.DisplayText,
                    Bytes = pattern.Bytes,
                    Mask = pattern.Mask,
                    MatchCount = matches
                };
            }

            length--;
        }

        return null;
    }
}
