using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DogeDebugger.Core.Cache;

public partial class StaticCacheItem : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    public required string FullPath { get; init; }

    public required string FileName { get; init; }

    public bool IsDump { get; init; }

    public string KindText => IsDump ? "转储" : "分析";

    public long SizeBytes { get; init; }

    public string SizeText => FormatSize(SizeBytes);

    public DateTime LastWriteTime { get; init; }

    public string LastWriteText =>
        LastWriteTime == default
            ? string.Empty
            : LastWriteTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public string DirectoryPath => Path.GetDirectoryName(FullPath) ?? string.Empty;

    public static StaticCacheItem FromFile(string fullPath)
    {
        string fileName = Path.GetFileName(fullPath);
        bool isDump = fileName.EndsWith(
            StaticAnalysisCacheStore.DumpSuffix,
            StringComparison.OrdinalIgnoreCase);
        long sizeBytes = 0;
        DateTime lastWriteTime = default;
        try
        {
            FileInfo file = new(fullPath);
            sizeBytes = file.Exists ? file.Length : 0;
            lastWriteTime = file.Exists ? file.LastWriteTime : default;
        }
        catch
        {
        }

        return new StaticCacheItem
        {
            FullPath = fullPath,
            FileName = fileName,
            IsDump = isDump,
            SizeBytes = sizeBytes,
            LastWriteTime = lastWriteTime
        };
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{bytes / 1024d / 1024d / 1024d:F2} GB");
        }

        if (bytes >= 1024L * 1024)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{bytes / 1024d / 1024d:F2} MB");
        }

        if (bytes >= 1024)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{bytes / 1024d:F1} KB");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{bytes} B");
    }
}
