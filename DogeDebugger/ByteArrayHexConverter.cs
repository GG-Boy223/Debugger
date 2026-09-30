using System.Globalization;
using System.Windows.Data;

namespace DogeDebugger;

public sealed class ByteArrayHexConverter : IValueConverter
{
    public static ByteArrayHexConverter Instance { get; } = new();

    public object Convert(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture)
    {
        return value is byte[] bytes
            ? string.Join(" ", bytes.Select(static item => item.ToString("X2", CultureInfo.InvariantCulture)))
            : string.Empty;
    }

    public object ConvertBack(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture) =>
        throw new NotSupportedException();
}
