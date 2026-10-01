using System.Globalization;
using System.Xml.Linq;
using DogeDebugger.Core.Modules;

namespace DogeDebugger.Core.Search;

public static class SavedAddressTableCodec
{
    private const int CheatEngineTableVersion = 46;

    public static string Serialize(IEnumerable<SavedAddressRowSource> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        XElement entries = new("CheatEntries");
        int id = 0;
        foreach (SavedAddressRowSource row in rows)
        {
            entries.Add(new XElement(
                "CheatEntry",
                new XElement("ID", id++),
                new XElement("Description", $"\"{row.Description}\""),
                new XElement("VariableType", GetVariableType(row.ValueKind)),
                new XElement("Address", $"0x{row.Address:X}"),
                new XElement("ShowAsHex", row.IsHexadecimal ? "1" : "0"),
                new XElement("ShowAsSigned", row.IsSigned ? "1" : "0"),
                new XElement("Value", row.Value)));
        }

        XDocument document = new(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(
                "CheatTable",
                new XAttribute("CheatEngineTableVersion", CheatEngineTableVersion),
                entries,
                new XElement("UserdefinedSymbols")));
        return document.ToString();
    }

    public static IReadOnlyList<SavedAddressRowSource> Deserialize(
        string xml,
        IReadOnlyList<ModuleDescriptor> modules)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xml);
        ArgumentNullException.ThrowIfNull(modules);

        XDocument document = XDocument.Parse(xml);
        XElement? entries = document.Root?.Element("CheatEntries");
        if (entries is null)
        {
            return [];
        }

        List<SavedAddressRowSource> rows = [];
        foreach (XElement entry in entries.Elements("CheatEntry"))
        {
            string addressText = entry.Element("Address")?.Value ?? string.Empty;
            if (!TryResolveAddress(addressText, modules, out ulong address))
            {
                continue;
            }

            string description = entry.Element("Description")?.Value ?? string.Empty;
            string valueType = entry.Element("VariableType")?.Value ?? "4 Bytes";
            rows.Add(new SavedAddressRowSource
            {
                Address = address,
                Description = description.Trim('"'),
                ValueKind = ParseVariableType(valueType),
                Value = entry.Element("Value")?.Value ?? string.Empty,
                IsHexadecimal = entry.Element("ShowAsHex")?.Value == "1",
                IsSigned = entry.Element("ShowAsSigned")?.Value != "0"
            });
        }

        return rows;
    }

    public static string GetVariableType(MemoryValueKind kind) => kind switch
    {
        MemoryValueKind.Byte => "Byte",
        MemoryValueKind.Int16 => "2 Bytes",
        MemoryValueKind.Int32 => "4 Bytes",
        MemoryValueKind.Int64 => "8 Bytes",
        MemoryValueKind.Single => "Float",
        MemoryValueKind.Double => "Double",
        MemoryValueKind.Utf8String => "String",
        MemoryValueKind.Utf16String => "Unicode String",
        MemoryValueKind.ByteArray => "Array of byte",
        _ => "4 Bytes"
    };

    public static MemoryValueKind ParseVariableType(string value)
    {
        string normalized = value.Trim();
        return normalized.ToUpperInvariant() switch
        {
            "BYTE" => MemoryValueKind.Byte,
            "2 BYTES" => MemoryValueKind.Int16,
            "4 BYTES" => MemoryValueKind.Int32,
            "8 BYTES" => MemoryValueKind.Int64,
            "FLOAT" => MemoryValueKind.Single,
            "DOUBLE" => MemoryValueKind.Double,
            "STRING" => MemoryValueKind.Utf8String,
            "UNICODE STRING" => MemoryValueKind.Utf16String,
            "ARRAY OF BYTE" or "ARRAY OF BYTES" => MemoryValueKind.ByteArray,
            _ => MemoryValueKind.Int32
        };
    }

    private static bool TryResolveAddress(
        string text,
        IReadOnlyList<ModuleDescriptor> modules,
        out ulong address)
    {
        address = 0;
        string value = text.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        int plusIndex = value.LastIndexOf('+');
        if (plusIndex > 0)
        {
            string moduleName = value[..plusIndex].Trim().Trim('"');
            string offsetText = value[(plusIndex + 1)..].Trim();
            ModuleDescriptor? module = modules.FirstOrDefault(
                candidate => string.Equals(
                    candidate.Name,
                    moduleName,
                    StringComparison.OrdinalIgnoreCase));
            if (module is not null &&
                TryParseInteger(offsetText, out ulong offset))
            {
                address = module.BaseAddress + offset;
                return true;
            }
        }

        return TryParseInteger(value, out address);
    }

    public static bool TryParseAddress(string text, out ulong address) =>
        TryParseInteger(text, out address);

    private static bool TryParseInteger(string text, out ulong value)
    {
        string normalized = text.Trim();
        if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[2..];
            return ulong.TryParse(
                normalized,
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out value);
        }

        return ulong.TryParse(
                   normalized,
                   NumberStyles.Integer,
                   CultureInfo.InvariantCulture,
                   out value) ||
               ulong.TryParse(
                   normalized,
                   NumberStyles.AllowHexSpecifier,
                   CultureInfo.InvariantCulture,
                   out value);
    }
}

public sealed class SavedAddressRowSource
{
    public required ulong Address { get; init; }

    public string Description { get; init; } = string.Empty;

    public MemoryValueKind ValueKind { get; init; } = MemoryValueKind.Int32;

    public string Value { get; init; } = string.Empty;

    public bool IsHexadecimal { get; init; }

    public bool IsSigned { get; init; }
}
