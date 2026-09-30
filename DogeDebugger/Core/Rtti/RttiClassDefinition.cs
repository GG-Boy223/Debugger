using System.Collections.ObjectModel;

namespace DogeDebugger.Core.Rtti;

public sealed class RttiClassDefinition
{
    private readonly ObservableCollection<RttiFieldDefinition> _fields = [];

    public Guid Id { get; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public string Comment { get; set; } = string.Empty;

    public ulong BaseAddress { get; set; }

    public bool IsExpanded { get; set; } = true;

    public ObservableCollection<RttiFieldDefinition> Fields => _fields;

    public string BaseAddressText => BaseAddress == 0 ? "-" : $"0x{BaseAddress:X}";

    public int FieldCount => _fields.Count;

    public int GetSize(bool is64Bit)
    {
        long size = 0;
        foreach (RttiFieldDefinition field in _fields)
        {
            size += Math.Max(field.GetSize(is64Bit), 0);
            if (size >= int.MaxValue)
            {
                return int.MaxValue;
            }
        }

        return checked((int)size);
    }

    public void RecalculateOffsets(bool is64Bit)
    {
        int offset = 0;
        foreach (RttiFieldDefinition field in _fields)
        {
            field.Offset = offset;
            offset = checked(offset + Math.Max(field.GetSize(is64Bit), 0));
        }
    }
}
