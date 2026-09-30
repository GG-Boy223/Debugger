using DogeDebugger.Core.Disassembly;
using DogeDebugger.Core.Modules;

namespace DogeDebugger.Core.Signatures.CrossVersion;

/// <summary>
/// Builds instruction-aware AOB patterns from a mapped module and searches
/// another mapped module for matches. Operand bytes reported by the
/// disassembler (displacements, immediates and branch targets) are wildcarded,
/// which makes the pattern tolerant to relocated addresses while keeping the
/// opcode and ModRM bytes strict.
/// </summary>
public sealed class InstructionAobLocator
{
    private const int MaximumPatternLength = 128;

    private const int MaximumMatches = 64;

    private readonly DisassemblerService _disassembler = new();

    public InstructionAobPattern? Build(
        LoadedPeImage image,
        uint rva,
        int maximumInstructions,
        int targetFixedBytes)
    {
        if (maximumInstructions <= 0 || targetFixedBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumInstructions));
        }

        byte[] available = image.ReadBytes(rva, 1024);
        if (available.Length == 0)
        {
            return null;
        }

        IReadOnlyList<InstructionSnapshot> instructions = _disassembler.Disassemble(
            available,
            image.ImageBase + rva,
            maximumInstructions,
            image.Is64Bit);
        if (instructions.Count == 0)
        {
            return null;
        }

        List<byte> bytes = [];
        List<byte> mask = [];
        int fixedBytes = 0;
        foreach (InstructionSnapshot instruction in instructions)
        {
            if (instruction.Bytes.Length == 0)
            {
                break;
            }

            for (int index = 0; index < instruction.Bytes.Length; index++)
            {
                byte byteMask = index < instruction.FixedByteMask.Count
                    ? instruction.FixedByteMask[index]
                    : (byte)1;
                bytes.Add(instruction.Bytes[index]);
                mask.Add(byteMask);
                if (byteMask != 0)
                {
                    fixedBytes++;
                }
            }

            if (fixedBytes >= targetFixedBytes || bytes.Count >= MaximumPatternLength)
            {
                break;
            }
        }

        if (fixedBytes < targetFixedBytes)
        {
            return null;
        }

        return new InstructionAobPattern
        {
            Bytes = bytes.ToArray(),
            Mask = mask.ToArray(),
            FixedByteCount = fixedBytes,
            Text = FormatPattern(bytes, mask)
        };
    }

    public IReadOnlyList<uint> FindMatches(
        LoadedPeImage image,
        InstructionAobPattern pattern,
        CancellationToken cancellationToken)
    {
        if (pattern.Bytes.Length == 0 || pattern.Bytes.Length != pattern.Mask.Length)
        {
            return [];
        }

        List<uint> matches = [];
        foreach (PeSection section in image.Metadata.Sections.Where(
                     static section => section.IsExecutable))
        {
            cancellationToken.ThrowIfCancellationRequested();
            int sectionOffset = checked((int)section.VirtualAddress);
            int sectionLength = checked((int)Math.Min(
                Math.Max(section.VirtualSize, section.RawSize),
                (uint)Math.Max(0, image.ImageBytes.Length - sectionOffset)));
            if (sectionLength < pattern.Bytes.Length)
            {
                continue;
            }

            ReadOnlySpan<byte> bytes = image.ImageBytes.AsSpan(sectionOffset, sectionLength);
            int last = bytes.Length - pattern.Bytes.Length;
            for (int offset = 0; offset <= last; offset++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool match = true;
                for (int index = 0; index < pattern.Bytes.Length; index++)
                {
                    if (pattern.Mask[index] != 0 &&
                        bytes[offset + index] != pattern.Bytes[index])
                    {
                        match = false;
                        break;
                    }
                }

                if (!match)
                {
                    continue;
                }

                matches.Add(section.VirtualAddress + (uint)offset);
                if (matches.Count >= MaximumMatches)
                {
                    return matches;
                }
            }
        }

        return matches;
    }

    private static string FormatPattern(
        IReadOnlyList<byte> bytes,
        IReadOnlyList<byte> mask)
    {
        return string.Join(
            ' ',
            bytes.Select((value, index) =>
                index < mask.Count && mask[index] == 0
                    ? "??"
                    : value.ToString("X2")));
    }
}

public sealed class InstructionAobPattern
{
    public required byte[] Bytes { get; init; }

    public required byte[] Mask { get; init; }

    public required int FixedByteCount { get; init; }

    public required string Text { get; init; }
}
