namespace DogeDebugger.Core.CrossReference;

public readonly record struct FunctionEntry(
    uint StartRva,
    uint EndRva,
    string? Name,
    FunctionSource Source)
{
    public string DisplayName =>
        string.IsNullOrWhiteSpace(Name) ? $"sub_{StartRva:X}" : Name;

    public uint Size => EndRva > StartRva ? EndRva - StartRva : 0;
}
