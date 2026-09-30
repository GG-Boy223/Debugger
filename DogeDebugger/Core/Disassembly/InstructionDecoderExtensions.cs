namespace DogeDebugger.Core.Disassembly;

public static class InstructionDecoderExtensions
{
    public static string Format(this InstructionSnapshot instruction, string? comment = null)
    {
        string suffix = string.IsNullOrWhiteSpace(comment) ? string.Empty : $" ; {comment}";
        return $"{instruction.Address:X16}  {instruction.Text}{suffix}";
    }
}
