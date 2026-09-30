using System.Globalization;
using System.Text;

namespace DogeDebugger.Core.Search;

public sealed class BytePattern
{
    private BytePattern(byte[] bytes, bool[] mask, string displayText)
    {
        Bytes = bytes;
        Mask = mask;
        DisplayText = displayText;
    }

    public byte[] Bytes { get; }

    public bool[] Mask { get; }

    public string DisplayText { get; }

    public int Length => Bytes.Length;

    public bool Matches(ReadOnlySpan<byte> candidate)
    {
        if (candidate.Length < Bytes.Length)
        {
            return false;
        }

        for (int index = 0; index < Bytes.Length; index++)
        {
            if (Mask[index] && candidate[index] != Bytes[index])
            {
                return false;
            }
        }

        return true;
    }

    public static bool TryParse(string text, out BytePattern? pattern, out string error)
    {
        pattern = null;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "The byte pattern is empty.";
            return false;
        }

        string normalized = text
            .Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("\\x", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(',', ' ')
            .Replace(';', ' ')
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();

        if (normalized.StartsWith('[') && normalized.EndsWith(']'))
        {
            normalized = normalized[1..^1];
        }

        string[] tokens = normalized.Split(
            [' ', '\t'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (tokens.Length == 0)
        {
            error = "The byte pattern does not contain any bytes.";
            return false;
        }

        List<byte> bytes = new(tokens.Length);
        List<bool> mask = new(tokens.Length);
        StringBuilder display = new();

        foreach (string token in tokens)
        {
            if (token is "?" or "??" or "*" or "xx" or "XX")
            {
                bytes.Add(0);
                mask.Add(false);
                display.Append("?? ");
                continue;
            }

            string value = token;
            if (value.StartsWith('0') &&
                value.Length > 2 &&
                (value[1] is 'x' or 'X'))
            {
                value = value[2..];
            }

            if (!byte.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out byte parsed))
            {
                error = $"Invalid byte token: {token}";
                return false;
            }

            bytes.Add(parsed);
            mask.Add(true);
            display.Append(parsed.ToString("X2", CultureInfo.InvariantCulture)).Append(' ');
        }

        pattern = new BytePattern(bytes.ToArray(), mask.ToArray(), display.ToString().TrimEnd());
        return true;
    }

    public static BytePattern FromMask(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> fixedMask)
    {
        if (bytes.Length != fixedMask.Length)
        {
            throw new ArgumentException("Byte and mask lengths must match.", nameof(fixedMask));
        }

        byte[] values = bytes.ToArray();
        bool[] mask = new bool[fixedMask.Length];
        StringBuilder display = new();
        for (int index = 0; index < fixedMask.Length; index++)
        {
            mask[index] = fixedMask[index] != 0;
            display.Append(
                mask[index]
                    ? values[index].ToString("X2", CultureInfo.InvariantCulture)
                    : "?");
            display.Append(' ');
        }

        return new BytePattern(values, mask, display.ToString().TrimEnd());
    }

    public int Match(ReadOnlySpan<byte> candidate) => Matches(candidate) ? 0 : -1;
}
