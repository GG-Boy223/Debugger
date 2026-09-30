using System.Text;

namespace DogeDebugger.UI.ViewModels.Panels;

public enum StringEncodingKind
{
    Utf8,
    Gbk,
    Ascii,
    Utf16,
    Big5,
    Utf16BigEndian,
    Utf32,
    Utf32BigEndian,
    ShiftJis,
    EucKr,
    Gb18030,
    Latin1,
    Windows1252,
    Custom
}

public sealed record StringEncodingOption(
    string DisplayName,
    StringEncodingKind Value);

public static class StringEncodingCatalog
{
    static StringEncodingCatalog()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static Encoding Resolve(
        StringEncodingKind kind,
        int customCodePage)
    {
        return kind switch
        {
            StringEncodingKind.Utf8 => Encoding.UTF8,
            StringEncodingKind.Gbk => Encoding.GetEncoding(936),
            StringEncodingKind.Ascii => Encoding.ASCII,
            StringEncodingKind.Utf16 => Encoding.Unicode,
            StringEncodingKind.Big5 => Encoding.GetEncoding(950),
            StringEncodingKind.Utf16BigEndian => Encoding.BigEndianUnicode,
            StringEncodingKind.Utf32 => Encoding.UTF32,
            StringEncodingKind.Utf32BigEndian => new UTF32Encoding(
                bigEndian: true,
                byteOrderMark: true),
            StringEncodingKind.ShiftJis => Encoding.GetEncoding(932),
            StringEncodingKind.EucKr => Encoding.GetEncoding(51949),
            StringEncodingKind.Gb18030 => Encoding.GetEncoding(54936),
            StringEncodingKind.Latin1 => Encoding.Latin1,
            StringEncodingKind.Windows1252 => Encoding.GetEncoding(1252),
            StringEncodingKind.Custom => Encoding.GetEncoding(customCodePage),
            _ => Encoding.UTF8
        };
    }

    public static bool IsValidCodePage(int codePage)
    {
        try
        {
            _ = Encoding.GetEncoding(codePage);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }
}
