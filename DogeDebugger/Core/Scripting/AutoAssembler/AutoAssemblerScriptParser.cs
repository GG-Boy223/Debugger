using System.Text;

namespace DogeDebugger.Core.Scripting.AutoAssembler;

/// <summary>
/// Splits an Auto Assembler script into the [ENABLE]/[DISABLE] section that is
/// being compiled, strips comments, and rewrites anonymous labels. The logic
/// reproduces the original parser's observable behavior:
/// lines before the first section marker apply to both sections, and a script
/// without any section marker is compiled as a whole.
/// </summary>
internal static class AutoAssemblerScriptParser
{
    public static List<AutoAssemblerScriptLine> ExtractSection(string script, bool enable)
    {
        string[] rawLines = (script ?? string.Empty).Split('\n', StringSplitOptions.None);
        List<AutoAssemblerScriptLine> prelude = [];
        List<AutoAssemblerScriptLine> section = [];
        string targetHeader = enable ? "[ENABLE]" : "[DISABLE]";
        string otherHeader = enable ? "[DISABLE]" : "[ENABLE]";
        bool insideTarget = false;
        bool sawHeader = false;
        bool sawTargetHeader = false;

        for (int index = 0; index < rawLines.Length; index++)
        {
            string trimmed = rawLines[index].TrimEnd('\r').Trim();
            if (string.Equals(trimmed, targetHeader, StringComparison.Ordinal))
            {
                if (sawTargetHeader)
                {
                    throw new AutoAssemblerException(
                        enable ? "只能有一个 [ENABLE] 段" : "只能有一个 [DISABLE] 段",
                        index + 1);
                }

                insideTarget = true;
                sawHeader = true;
                sawTargetHeader = true;
            }
            else if (string.Equals(trimmed, otherHeader, StringComparison.Ordinal))
            {
                insideTarget = false;
                sawHeader = true;
            }
            else if (!sawHeader)
            {
                prelude.Add(new AutoAssemblerScriptLine(rawLines[index].TrimEnd('\r'), index + 1));
            }
            else if (insideTarget)
            {
                section.Add(new AutoAssemblerScriptLine(rawLines[index].TrimEnd('\r'), index + 1));
            }
        }

        if (!sawHeader)
        {
            List<AutoAssemblerScriptLine> whole = [];
            for (int index = 0; index < rawLines.Length; index++)
            {
                whole.Add(new AutoAssemblerScriptLine(rawLines[index].TrimEnd('\r'), index + 1));
            }

            return whole;
        }

        prelude.AddRange(section);
        return prelude;
    }

    /// <summary>
    /// Removes comments (`//`, `{...}`, `/*...*/`) outside of single quotes
    /// and trims the resulting line. Empty results are returned so callers can
    /// skip them.
    /// </summary>
    public static void StripComments(IList<AutoAssemblerScriptLine> lines)
    {
        for (int index = 0; index < lines.Count; index++)
        {
            char[] characters = lines[index].Text.ToCharArray();
            bool insideBlockComment = false;
            bool insideBraceComment = false;
            bool insideQuote = false;
            bool truncated = false;
            int length = characters.Length;

            for (int position = 0; position < characters.Length; position++)
            {
                char current = characters[position];
                if (insideBlockComment || insideBraceComment)
                {
                    if (insideBraceComment && current == '}')
                    {
                        characters[position] = ' ';
                        insideBraceComment = false;
                    }
                    else if (insideBlockComment &&
                             current == '*' &&
                             position + 1 < characters.Length &&
                             characters[position + 1] == '/')
                    {
                        characters[position] = ' ';
                        characters[position + 1] = ' ';
                        insideBlockComment = false;
                        position++;
                    }
                    else
                    {
                        characters[position] = ' ';
                    }

                    continue;
                }

                if (current == '\'')
                {
                    insideQuote = !insideQuote;
                }

                if (current == '\t')
                {
                    characters[position] = ' ';
                }

                if (insideQuote)
                {
                    continue;
                }

                if (current == '/' && position + 1 < characters.Length && characters[position + 1] == '/')
                {
                    length = position;
                    truncated = true;
                    break;
                }

                if (current == '{' &&
                    (position + 1 >= characters.Length || characters[position + 1] != '$'))
                {
                    insideBraceComment = true;
                    characters[position] = ' ';
                }
                else if (current == '/' && position + 1 < characters.Length && characters[position + 1] == '*')
                {
                    insideBlockComment = true;
                    characters[position] = ' ';
                    characters[position + 1] = ' ';
                    position++;
                }
            }

            string text = truncated
                ? new string(characters, 0, length)
                : new string(characters);
            lines[index] = new AutoAssemblerScriptLine(text.Trim(), lines[index].LineNumber);
        }
    }

    /// <summary>
    /// Rewrites `@@:` anonymous labels to unique names and resolves `@F`/`@B`
    /// references against the nearest following/preceding label.
    /// </summary>
    public static void RewriteAnonymousLabels(IList<AutoAssemblerScriptLine> lines)
    {
        List<string> labels = [];
        List<int> labelIndices = [];
        Random random = new();

        for (int index = 0; index < lines.Count; index++)
        {
            string text = lines[index].Text;
            if (string.Equals(text, "@@:", StringComparison.Ordinal))
            {
                string generated = $"_anon_{random.Next():X8}_{index}";
                lines[index] = new AutoAssemblerScriptLine(generated + ":", lines[index].LineNumber);
                labels.Add(generated);
                labelIndices.Add(index);
            }
            else if (IsPlainLabel(text))
            {
                labels.Add(text[..^1]);
                labelIndices.Add(index);
            }
        }

        int currentLabel = -1;
        for (int index = 0; index < lines.Count; index++)
        {
            string text = lines[index].Text;
            if (IsPlainLabel(text))
            {
                string name = text[..^1];
                for (int search = currentLabel + 1; search < labels.Count; search++)
                {
                    if (string.Equals(name, labels[search], StringComparison.OrdinalIgnoreCase))
                    {
                        currentLabel = search;
                        break;
                    }
                }

                continue;
            }

            if (text.Contains("@F", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("@B", StringComparison.OrdinalIgnoreCase))
            {
                if (text.Contains("@F", StringComparison.OrdinalIgnoreCase) &&
                    currentLabel + 1 < labels.Count)
                {
                    text = ReplaceToken(text, "@F", labels[currentLabel + 1]);
                    text = ReplaceToken(text, "@f", labels[currentLabel + 1]);
                }

                if (text.Contains("@B", StringComparison.OrdinalIgnoreCase) && currentLabel >= 0)
                {
                    text = ReplaceToken(text, "@B", labels[currentLabel]);
                    text = ReplaceToken(text, "@b", labels[currentLabel]);
                }

                lines[index] = new AutoAssemblerScriptLine(text, lines[index].LineNumber);
            }
        }
    }

    /// <summary>
    /// Collects label names declared as `name:` where the name is not a plain
    /// hexadecimal address and contains no '+' or white space.
    /// </summary>
    public static HashSet<string> CollectLocalLabels(IEnumerable<AutoAssemblerScriptLine> lines)
    {
        HashSet<string> labels = new(StringComparer.OrdinalIgnoreCase);
        foreach (AutoAssemblerScriptLine line in lines)
        {
            string text = line.Text.Trim();
            if (text.Length <= 1 || text[^1] != ':' || text.Contains('+') || text.Contains('.'))
            {
                continue;
            }

            string name = text[..^1];
            if (!ulong.TryParse(
                    name,
                    System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out _))
            {
                labels.Add(name);
            }
        }

        return labels;
    }

    private static bool IsPlainLabel(string text)
    {
        string trimmed = text.Trim();
        return trimmed.Length > 1 &&
               trimmed[^1] == ':' &&
               !trimmed.Contains('+') &&
               !trimmed.Contains(' ') &&
               !trimmed.Contains('.');
    }

    private static string ReplaceToken(string text, string token, string replacement)
    {
        StringBuilder builder = new(text.Length);
        int position = 0;
        while (position < text.Length)
        {
            int found = text.IndexOf(token, position, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
            {
                builder.Append(text, position, text.Length - position);
                break;
            }

            builder.Append(text, position, found - position);
            builder.Append(replacement);
            position = found + token.Length;
        }

        return builder.ToString();
    }
}
