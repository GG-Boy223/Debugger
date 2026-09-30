using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace DogeDebugger.Core.Search;

internal sealed class MemoryValueSample
{
    private readonly MemoryValueKind _kind;
    private readonly decimal? _integer;
    private readonly double? _floating;
    private readonly byte[] _bytes;
    private readonly bool _ignoreCase;
    private readonly Encoding _encoding;

    private MemoryValueSample(
        MemoryValueKind kind,
        decimal? integer,
        double? floating,
        byte[] bytes,
        bool ignoreCase,
        Encoding? encoding)
    {
        _kind = kind;
        _integer = integer;
        _floating = floating;
        _bytes = bytes;
        _ignoreCase = ignoreCase;
        _encoding = encoding ?? Encoding.UTF8;
    }

    internal static bool TryParse(
        string text,
        MemoryValueKind kind,
        bool ignoreCase,
        out MemoryValueSample? sample)
    {
        return TryParse(text, kind, ignoreCase, textEncoding: null, out sample);
    }

    internal static bool TryParse(
        string text,
        MemoryValueKind kind,
        bool ignoreCase,
        Encoding? textEncoding,
        out MemoryValueSample? sample)
    {
        sample = null;
        if (string.IsNullOrEmpty(text) && !IsString(kind))
        {
            return false;
        }

        if (IsString(kind))
        {
            Encoding encoding = textEncoding ??
                (kind == MemoryValueKind.Utf16String
                    ? Encoding.Unicode
                    : Encoding.UTF8);
            return TryCreate(
                kind,
                integer: null,
                floating: null,
                encoding.GetBytes(text),
                ignoreCase,
                encoding,
                out sample);
        }

        string normalized = text.Trim();
        if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[2..];
            if (!ulong.TryParse(
                    normalized,
                    NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture,
                    out ulong unsigned))
            {
                return false;
            }

            if (kind is MemoryValueKind.Single or MemoryValueKind.Double)
            {
                return false;
            }

            decimal hex = unsigned;
            return TryCreate(
                kind,
                hex,
                floating: null,
                BuildNumericBytes(kind, hex),
                ignoreCase,
                encoding: null,
                out sample);
        }

        if (kind is MemoryValueKind.Single or MemoryValueKind.Double)
        {
            if (!double.TryParse(
                    normalized,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double floating))
            {
                return false;
            }

            return TryCreate(
                kind,
                integer: null,
                floating,
                BuildFloatingBytes(kind, floating),
                ignoreCase,
                encoding: null,
                out sample);
        }

        if (!decimal.TryParse(
                normalized,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out decimal integerValue))
        {
            return false;
        }

        if (!IsInRange(kind, integerValue))
        {
            return false;
        }

        return TryCreate(
            kind,
            integerValue,
            floating: null,
            BuildNumericBytes(kind, integerValue),
            ignoreCase,
            encoding: null,
            out sample);
    }

    internal static bool TryCreate(
        MemoryValueKind kind,
        ReadOnlySpan<byte> bytes,
        bool ignoreCase,
        out MemoryValueSample? sample)
    {
        return TryCreate(kind, bytes, ignoreCase, textEncoding: null, out sample);
    }

    internal static bool TryCreate(
        MemoryValueKind kind,
        ReadOnlySpan<byte> bytes,
        bool ignoreCase,
        Encoding? textEncoding,
        out MemoryValueSample? sample)
    {
        sample = null;
        byte[] copy = bytes.ToArray();

        if (IsString(kind))
        {
            sample = new MemoryValueSample(
                kind,
                null,
                null,
                copy,
                ignoreCase,
                textEncoding);
            return true;
        }

        if (copy.Length != SizeOf(kind))
        {
            return false;
        }

        if (kind is MemoryValueKind.Single or MemoryValueKind.Double)
        {
            double value = kind == MemoryValueKind.Single
                ? BitConverter.ToSingle(copy)
                : BitConverter.ToDouble(copy);
            sample = new MemoryValueSample(
                kind,
                null,
                value,
                copy,
                ignoreCase,
                encoding: null);
            return true;
        }

        decimal integer = kind switch
        {
            MemoryValueKind.Byte => copy[0],
            MemoryValueKind.SByte => unchecked((sbyte)copy[0]),
            MemoryValueKind.Int16 => BitConverter.ToInt16(copy),
            MemoryValueKind.UInt16 => BitConverter.ToUInt16(copy),
            MemoryValueKind.Int32 => BitConverter.ToInt32(copy),
            MemoryValueKind.UInt32 => BitConverter.ToUInt32(copy),
            MemoryValueKind.Int64 => BitConverter.ToInt64(copy),
            MemoryValueKind.UInt64 => BitConverter.ToUInt64(copy),
            _ => 0
        };
        sample = new MemoryValueSample(
            kind,
            integer,
            null,
            copy,
            ignoreCase,
            encoding: null);
        return true;
    }

    internal int Size => _bytes.Length;

    internal byte[] Bytes => _bytes.ToArray();

    internal bool Matches(
        MemoryValueSample current,
        MemoryValueComparison comparison,
        MemoryValueSample? second)
    {
        if (_kind != current._kind)
        {
            return false;
        }

        if (IsString(_kind))
        {
            string expected = DecodeString(_bytes);
            string actual = DecodeString(current._bytes);
            StringComparison stringComparison = _ignoreCase
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            bool equals = string.Equals(expected, actual, stringComparison);
            return comparison switch
            {
                MemoryValueComparison.Exact or MemoryValueComparison.Unchanged => equals,
                MemoryValueComparison.NotEqual or MemoryValueComparison.Changed => !equals,
                _ => false
            };
        }

        if (_kind is MemoryValueKind.Single or MemoryValueKind.Double)
        {
            double expected = _floating ?? 0;
            double actual = current._floating ?? 0;
            return CompareFloating(expected, actual, comparison, second?._floating ?? 0);
        }

        decimal target = _integer ?? 0;
        decimal observed = current._integer ?? 0;
        return CompareInteger(target, observed, comparison, second?._integer ?? 0);
    }

    internal bool MatchesDifference(
        MemoryValueSample previous,
        MemoryValueSample current,
        bool decreasing)
    {
        if (_kind != previous._kind || _kind != current._kind || IsString(_kind))
        {
            return false;
        }

        if (_kind is MemoryValueKind.Single or MemoryValueKind.Double)
        {
            double previousValue = previous._floating ?? 0;
            double currentValue = current._floating ?? 0;
            double expectedDelta = _floating ?? 0;
            double actualDelta = decreasing
                ? previousValue - currentValue
                : currentValue - previousValue;
            double scale = Math.Max(
                1d,
                Math.Max(Math.Abs(actualDelta), Math.Abs(expectedDelta)));
            return Math.Abs(actualDelta - expectedDelta) <= scale * 1e-12;
        }

        decimal previousInteger = previous._integer ?? 0;
        decimal currentInteger = current._integer ?? 0;
        decimal expectedInteger = _integer ?? 0;
        decimal actualInteger = decreasing
            ? previousInteger - currentInteger
            : currentInteger - previousInteger;
        return actualInteger == expectedInteger;
    }

    internal static int SizeOf(MemoryValueKind kind) => kind switch
    {
        MemoryValueKind.Byte or MemoryValueKind.SByte => sizeof(byte),
        MemoryValueKind.Int16 or MemoryValueKind.UInt16 => sizeof(short),
        MemoryValueKind.Int32 or MemoryValueKind.UInt32 => sizeof(int),
        MemoryValueKind.Int64 or MemoryValueKind.UInt64 => sizeof(long),
        MemoryValueKind.Single => sizeof(float),
        MemoryValueKind.Double => sizeof(double),
        _ => 0
    };

    internal static bool IsString(MemoryValueKind kind) =>
        kind is MemoryValueKind.Utf8String or MemoryValueKind.Utf16String;

    private static bool TryCreate(
        MemoryValueKind kind,
        decimal? integer,
        double? floating,
        byte[] bytes,
        bool ignoreCase,
        Encoding? encoding,
        out MemoryValueSample? sample)
    {
        sample = new MemoryValueSample(
            kind,
            integer,
            floating,
            bytes,
            ignoreCase,
            encoding);
        return true;
    }

    private static byte[] BuildNumericBytes(MemoryValueKind kind, decimal value)
    {
        return kind switch
        {
            MemoryValueKind.Byte => [(byte)value],
            MemoryValueKind.SByte => [unchecked((byte)(sbyte)value)],
            MemoryValueKind.Int16 => BitConverter.GetBytes((short)value),
            MemoryValueKind.UInt16 => BitConverter.GetBytes((ushort)value),
            MemoryValueKind.Int32 => BitConverter.GetBytes((int)value),
            MemoryValueKind.UInt32 => BitConverter.GetBytes((uint)value),
            MemoryValueKind.Int64 => BitConverter.GetBytes((long)value),
            MemoryValueKind.UInt64 => BitConverter.GetBytes((ulong)value),
            _ => []
        };
    }

    private static byte[] BuildFloatingBytes(MemoryValueKind kind, double value) =>
        kind == MemoryValueKind.Single
            ? BitConverter.GetBytes((float)value)
            : BitConverter.GetBytes(value);

    private static bool IsInRange(MemoryValueKind kind, decimal value) => kind switch
    {
        MemoryValueKind.Byte => value is >= byte.MinValue and <= byte.MaxValue,
        MemoryValueKind.SByte => value is >= sbyte.MinValue and <= sbyte.MaxValue,
        MemoryValueKind.Int16 => value is >= short.MinValue and <= short.MaxValue,
        MemoryValueKind.UInt16 => value is >= ushort.MinValue and <= ushort.MaxValue,
        MemoryValueKind.Int32 => value is >= int.MinValue and <= int.MaxValue,
        MemoryValueKind.UInt32 => value is >= uint.MinValue and <= uint.MaxValue,
        MemoryValueKind.Int64 => value is >= long.MinValue and <= long.MaxValue,
        MemoryValueKind.UInt64 => value is >= ulong.MinValue and <= ulong.MaxValue,
        _ => false
    };

    private string DecodeString(byte[] bytes)
    {
        return _encoding.GetString(bytes);
    }

    private static bool CompareInteger(
        decimal expected,
        decimal observed,
        MemoryValueComparison comparison,
        decimal second)
    {
        bool changed = expected != observed;
        return comparison switch
        {
            MemoryValueComparison.Exact => observed == expected,
            MemoryValueComparison.NotEqual => observed != expected,
            MemoryValueComparison.GreaterThan => observed > expected,
            MemoryValueComparison.GreaterThanOrEqual => observed >= expected,
            MemoryValueComparison.LessThan => observed < expected,
            MemoryValueComparison.LessThanOrEqual => observed <= expected,
            MemoryValueComparison.Between => observed >= expected && observed <= second,
            MemoryValueComparison.Changed => changed,
            MemoryValueComparison.Unchanged => !changed,
            MemoryValueComparison.Increased => observed > expected,
            MemoryValueComparison.Decreased => observed < expected,
            _ => false
        };
    }

    private static bool CompareFloating(
        double expected,
        double observed,
        MemoryValueComparison comparison,
        double second)
    {
        bool changed = !expected.Equals(observed);
        return comparison switch
        {
            MemoryValueComparison.Exact => observed.Equals(expected),
            MemoryValueComparison.NotEqual => !observed.Equals(expected),
            MemoryValueComparison.GreaterThan => observed > expected,
            MemoryValueComparison.GreaterThanOrEqual => observed >= expected,
            MemoryValueComparison.LessThan => observed < expected,
            MemoryValueComparison.LessThanOrEqual => observed <= expected,
            MemoryValueComparison.Between => observed >= expected && observed <= second,
            MemoryValueComparison.Changed => changed,
            MemoryValueComparison.Unchanged => !changed,
            MemoryValueComparison.Increased => observed > expected,
            MemoryValueComparison.Decreased => observed < expected,
            _ => false
        };
    }
}
