using System.Globalization;
using System.Text;

namespace DogeDebugger.Core.Scripting.AutoAssembler;

public readonly record struct AutoAssemblerInstruction(string Text, int Length);

/// <summary>
/// Builds the four scripts offered by the 示例 drop-down. The generated text
/// is byte-for-byte compatible with the original templates.
/// </summary>
public static class AutoAssemblerTemplates
{
    public static string CreateBasicScript() =>
        "[ENABLE]\r\n\r\n\r\n[DISABLE]\r\n";

    public static string CreateInjectionScript(
        string addressText,
        IReadOnlyList<AutoAssemblerInstruction> originalInstructions,
        int jumpLength)
    {
        string newLine = Environment.NewLine;
        (string originalCode, int totalBytes) = BuildOriginalCode(originalInstructions, jumpLength);
        int paddingCount = totalBytes - jumpLength;

        StringBuilder builder = new();
        builder.Append("alloc(newmem,2048,").Append(addressText).Append(')').Append(newLine);
        builder.Append("label(returnhere)").Append(newLine);
        builder.Append("label(originalcode)").Append(newLine);
        builder.Append("label(exit)").Append(newLine);
        builder.Append(newLine);
        builder.Append("newmem: //this is allocated memory, you have read,write,execute access").Append(newLine);
        builder.Append("  //place your code here").Append(newLine);
        builder.Append(newLine);
        builder.Append("originalcode:").Append(newLine);
        builder.Append(originalCode);
        builder.Append(newLine);
        builder.Append("exit:").Append(newLine);
        builder.Append("jmp returnhere").Append(newLine);
        builder.Append(newLine);
        builder.Append(addressText).Append(':').Append(newLine);
        builder.Append("jmp newmem").Append(newLine);
        for (int index = 0; index < paddingCount; index++)
        {
            builder.Append("nop").Append(newLine);
        }

        builder.Append("returnhere:").Append(newLine);
        return builder.ToString();
    }

    public static string CreateAobScript(
        string symbol,
        string moduleName = "",
        string pattern = "")
    {
        string scanLine = string.IsNullOrEmpty(moduleName)
            ? $"aobscan({symbol},{pattern})"
            : $"aobscanmodule({symbol},{moduleName},{pattern})";

        return
            "{ Game  : " + Environment.NewLine +
            "  Version:" + Environment.NewLine +
            "  Date  : " + DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + Environment.NewLine +
            "  Author : " + Environment.UserName + Environment.NewLine +
            "  Description: " + Environment.NewLine +
            "}" + Environment.NewLine +
            "[ENABLE]" + Environment.NewLine +
            Environment.NewLine +
            scanLine + Environment.NewLine +
            Environment.NewLine +
            "alloc(newmem,$1000)" + Environment.NewLine +
            "label(returnhere)" + Environment.NewLine +
            Environment.NewLine +
            "newmem:" + Environment.NewLine +
            Environment.NewLine +
            "  jmp returnhere" + Environment.NewLine +
            Environment.NewLine +
            symbol + ":" + Environment.NewLine +
            "  jmp newmem" + Environment.NewLine +
            "  nop" + Environment.NewLine +
            "returnhere:" + Environment.NewLine +
            Environment.NewLine +
            "registersymbol(" + symbol + ")" + Environment.NewLine +
            Environment.NewLine +
            "[DISABLE]" + Environment.NewLine +
            Environment.NewLine +
            symbol + ":" + Environment.NewLine +
            "  db 90 90 90 90 90 90" + Environment.NewLine +
            Environment.NewLine +
            "unregistersymbol(" + symbol + ")" + Environment.NewLine +
            "dealloc(newmem)";
    }

    private static (string Text, int TotalBytes) BuildOriginalCode(
        IReadOnlyList<AutoAssemblerInstruction> instructions,
        int jumpLength)
    {
        if (instructions.Count == 0)
        {
            return ("  //original code here\n", jumpLength);
        }

        StringBuilder builder = new();
        int totalBytes = 0;
        foreach (AutoAssemblerInstruction instruction in instructions)
        {
            if (totalBytes >= jumpLength)
            {
                break;
            }

            builder.Append("  ").Append(instruction.Text).Append(Environment.NewLine);
            totalBytes += instruction.Length;
        }

        if (totalBytes < jumpLength)
        {
            totalBytes = jumpLength;
        }

        return (builder.ToString(), totalBytes);
    }
}
