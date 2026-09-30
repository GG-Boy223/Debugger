using System.IO;

namespace DogeDebugger.Core.Cache;

/// <summary>
/// Locates and maintains the static-analysis cache tree under
/// <c>Saved/Analysis</c>, matching the layout used by the original
/// DogeDebugger build.
/// </summary>
public static class StaticAnalysisCacheStore
{
    public const string AnalyzeSuffix = "_analyze.bin";

    public const string DumpSuffix = "_dump.bin";

    public static string SavedRoot { get; } = Path.Combine(
        AppContext.BaseDirectory,
        "Saved");

    public static string AnalysisRoot { get; } = Path.Combine(
        SavedRoot,
        "Analysis");

    public static string GetModuleDirectory(string? moduleName)
    {
        string name = NormalizeModuleName(moduleName);
        return Path.Combine(AnalysisRoot, name);
    }

    public static string GetAnalyzePath(string? moduleName, string fileHash)
    {
        return Path.Combine(
            GetModuleDirectory(moduleName),
            BuildFileName(moduleName, fileHash, AnalyzeSuffix));
    }

    public static string GetDumpPath(string? moduleName, string fileHash)
    {
        return Path.Combine(
            GetModuleDirectory(moduleName),
            BuildFileName(moduleName, fileHash, DumpSuffix));
    }

    public static IReadOnlyList<string> EnumerateAnalyzeFiles()
    {
        return EnumerateBySuffix("*" + AnalyzeSuffix);
    }

    public static IReadOnlyList<string> EnumerateDumpFiles()
    {
        return EnumerateBySuffix("*" + DumpSuffix);
    }

    public static IReadOnlyList<string> EnumerateAllFiles()
    {
        MigrateLegacyLayout();
        return EnumerateAnalyzeFiles()
            .Concat(EnumerateDumpFiles())
            .OrderByDescending(GetLastWriteTimeSafe)
            .ToArray();
    }

    public static long GetTotalSizeBytes()
    {
        MigrateLegacyLayout();
        long total = 0;
        foreach (string path in EnumerateAnalyzeFiles().Concat(EnumerateDumpFiles()))
        {
            try
            {
                total += new FileInfo(path).Length;
            }
            catch
            {
            }
        }

        return total;
    }

    /// <summary>
    /// Moves legacy cache files that live directly under
    /// <c>Saved/Analysis</c> into the per-module directory the current build
    /// expects. Existing destinations win and the duplicate source is removed.
    /// </summary>
    public static void MigrateLegacyLayout()
    {
        if (!Directory.Exists(AnalysisRoot))
        {
            return;
        }

        string[] legacyFiles;
        try
        {
            legacyFiles = Directory.GetFiles(
                AnalysisRoot,
                "*" + AnalyzeSuffix,
                SearchOption.TopDirectoryOnly);
        }
        catch
        {
            return;
        }

        foreach (string source in legacyFiles)
        {
            try
            {
                string fileName = Path.GetFileName(source);
                string stem = fileName[..^AnalyzeSuffix.Length];
                int separator = stem.IndexOf('_');
                string moduleName = separator > 0 ? stem[..separator] : stem;
                string targetDirectory = GetModuleDirectory(moduleName);
                Directory.CreateDirectory(targetDirectory);
                string target = Path.Combine(targetDirectory, fileName);
                if (File.Exists(target))
                {
                    File.Delete(source);
                }
                else
                {
                    File.Move(source, target);
                }
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// Returns true when <paramref name="path"/> is a regular file inside the
    /// managed analysis tree and neither the file nor any ancestor directory
    /// is a reparse point.
    /// </summary>
    public static bool IsManagedPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            string fullPath = Path.GetFullPath(path);
            string root = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(AnalysisRoot));
            if (!fullPath.StartsWith(
                    root + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string? directory = Path.GetDirectoryName(fullPath);
            while (!string.IsNullOrEmpty(directory) &&
                   directory.Length >= root.Length)
            {
                DirectoryInfo info = new(directory);
                if (info.Exists &&
                    (info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return false;
                }

                if (string.Equals(
                        directory,
                        root,
                        StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                directory = Path.GetDirectoryName(directory);
            }

            FileInfo file = new(fullPath);
            return file.Exists &&
                   (file.Attributes & FileAttributes.ReparsePoint) == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Deletes <paramref name="path"/> only when it is inside the managed
    /// analysis tree, then removes the containing module directory when it is
    /// empty.
    /// </summary>
    public static bool DeleteManagedFile(string? path)
    {
        if (!IsManagedPath(path))
        {
            return false;
        }

        string fullPath = Path.GetFullPath(path!);
        string directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
        try
        {
            File.Delete(fullPath);
        }
        catch
        {
            return false;
        }

        TryDeleteEmptyDirectory(directory);
        return true;
    }

    public static void ForgetEmptyModuleDirectory(string? directory)
    {
        TryDeleteEmptyDirectory(directory);
    }

    public static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
        {
            return string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{bytes / 1024d / 1024d / 1024d:F2} GB");
        }

        if (bytes >= 1024L * 1024)
        {
            return string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{bytes / 1024d / 1024d:F2} MB");
        }

        if (bytes >= 1024)
        {
            return string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{bytes / 1024d:F1} KB");
        }

        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{bytes} B");
    }

    private static IReadOnlyList<string> EnumerateBySuffix(string pattern)
    {
        if (!Directory.Exists(AnalysisRoot))
        {
            return [];
        }

        try
        {
            return Directory.GetFiles(
                AnalysisRoot,
                pattern,
                SearchOption.AllDirectories);
        }
        catch
        {
            return [];
        }
    }

    private static string BuildFileName(
        string? moduleName,
        string fileHash,
        string suffix)
    {
        return NormalizeModuleName(moduleName) + "_" + NormalizeHash(fileHash) + suffix;
    }

    private static string NormalizeModuleName(string? moduleName)
    {
        string name = string.IsNullOrWhiteSpace(moduleName)
            ? "_"
            : Path.GetFileName(moduleName.Trim());
        return string.IsNullOrWhiteSpace(name) ? "_" : name;
    }

    private static string NormalizeHash(string? fileHash)
    {
        string hash = (fileHash ?? string.Empty).Trim().ToUpperInvariant();
        if (hash.Length != 32 || !hash.All(Uri.IsHexDigit))
        {
            throw new ArgumentException(
                "Static-analysis file hash must be exactly 32 hexadecimal characters.",
                nameof(fileHash));
        }

        return hash;
    }

    private static DateTime GetLastWriteTimeSafe(string path)
    {
        try
        {
            return File.GetLastWriteTime(path);
        }
        catch
        {
            return default;
        }
    }

    private static void TryDeleteEmptyDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        try
        {
            string fullDirectory = Path.GetFullPath(directory);
            string root = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(AnalysisRoot));
            if (!fullDirectory.StartsWith(
                    root + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (Directory.Exists(fullDirectory) &&
                !Directory.EnumerateFileSystemEntries(fullDirectory).Any())
            {
                Directory.Delete(fullDirectory);
            }
        }
        catch
        {
        }
    }
}
