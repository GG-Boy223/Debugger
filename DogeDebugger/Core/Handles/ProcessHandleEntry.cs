namespace DogeDebugger.Core.Handles;

public sealed class ProcessHandleEntry
{
    public required uint ProcessId { get; init; }

    public required ulong HandleValue { get; init; }

    public required ulong ObjectAddress { get; init; }

    public required uint GrantedAccess { get; init; }

    public string TypeName { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string HandleValueText => $"0x{HandleValue:X}";

    public string ObjectAddressText => $"0x{ObjectAddress:X}";

    public string GrantedAccessText => $"0x{GrantedAccess:X8}";
}
