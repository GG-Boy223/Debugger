using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DogeDebugger.Core.Cache;

/// <summary>
/// Enumerates and validates static-analysis cache pairs. File naming follows
/// the original <c>&lt;module&gt;_&lt;32-hex-hash&gt;_(analyze|dump).bin</c>
/// convention and artifacts are sorted by module name then write time.
/// </summary>
public static class StaticAnalysisCacheArtifactStore
{
    private static readonly Regex ArtifactNamePattern = new(
        @"^(?<module>.+)_(?<hash>[0-9a-fA-F]{32})_(?<kind>analyze|dump)\.bin$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static IReadOnlyList<StaticAnalysisCacheArtifact> EnumerateArtifacts()
    {
        string root = StaticAnalysisCacheStore.AnalysisRoot;
        if (!Directory.Exists(root))
        {
            return [];
        }

        Dictionary<string, Candidate> candidates =
            new(StringComparer.OrdinalIgnoreCase);
        Stack<string> pending = new();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            string[] files;
            try
            {
                files = Directory.GetFiles(directory, "*.bin", SearchOption.TopDirectoryOnly);
            }
            catch
            {
                continue;
            }

            foreach (string file in files)
            {
                if (IsReparsePoint(file))
                {
                    continue;
                }

                Match match = ArtifactNamePattern.Match(Path.GetFileName(file));
                if (!match.Success)
                {
                    continue;
                }

                string moduleName = match.Groups["module"].Value;
                string hash = match.Groups["hash"].Value.ToUpperInvariant();
                bool isAnalyze = match.Groups["kind"].Value.Equals(
                    "analyze",
                    StringComparison.OrdinalIgnoreCase);
                string key = moduleName + "\n" + hash;
                if (!candidates.TryGetValue(key, out Candidate? candidate))
                {
                    candidate = new Candidate(moduleName, hash);
                    candidates.Add(key, candidate);
                }

                candidate.SetPath(file, isAnalyze);
            }

            string[] children;
            try
            {
                children = Directory.GetDirectories(directory);
            }
            catch
            {
                continue;
            }

            foreach (string child in children)
            {
                if (!IsReparsePoint(child))
                {
                    pending.Push(child);
                }
            }
        }

        List<StaticAnalysisCacheArtifact> artifacts = new(candidates.Count);
        foreach (Candidate candidate in candidates.Values)
        {
            artifacts.Add(candidate.ToArtifact());
        }

        return artifacts
            .OrderBy(static artifact => artifact.ModuleName, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(static artifact => artifact.LastWriteTime)
            .ToArray();
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return true;
        }
    }

    private static string BuildArtifactId(string moduleName, string fileHash)
    {
        byte[] digest = SHA256.HashData(
            Encoding.UTF8.GetBytes(moduleName + "\n" + fileHash));
        return Convert.ToHexString(digest)[..16];
    }

    private sealed class Candidate
    {
        private string? _analyzePath;
        private string? _dumpPath;
        private DateTime _analyzeWriteTime;
        private DateTime _dumpWriteTime;

        public Candidate(string moduleName, string fileHash)
        {
            ModuleName = moduleName;
            FileHash = fileHash;
        }

        public string ModuleName { get; }

        public string FileHash { get; }

        public void SetPath(string path, bool isAnalyze)
        {
            DateTime writeTime = GetWriteTime(path);
            if (isAnalyze)
            {
                if (_analyzePath is null || writeTime >= _analyzeWriteTime)
                {
                    _analyzePath = path;
                    _analyzeWriteTime = writeTime;
                }
            }
            else if (_dumpPath is null || writeTime >= _dumpWriteTime)
            {
                _dumpPath = path;
                _dumpWriteTime = writeTime;
            }
        }

        public StaticAnalysisCacheArtifact ToArtifact()
        {
            StaticAnalysisCacheHeader? header = null;
            if (_analyzePath is not null &&
                StaticAnalysisCacheHeader.TryRead(_analyzePath, out StaticAnalysisCacheHeader? read, out _) &&
                read is not null &&
                string.Equals(read.FileHash, FileHash, StringComparison.OrdinalIgnoreCase))
            {
                header = read;
            }

            DateTime lastWriteTime = _analyzeWriteTime;
            if (_dumpWriteTime > lastWriteTime)
            {
                lastWriteTime = _dumpWriteTime;
            }

            return new StaticAnalysisCacheArtifact
            {
                ArtifactId = BuildArtifactId(ModuleName, FileHash),
                ModuleName = header?.ModuleName is { Length: > 0 } name
                    ? name
                    : ModuleName,
                FileHash = FileHash,
                AnalyzePath = _analyzePath,
                DumpPath = _dumpPath,
                Header = header,
                LastWriteTime = lastWriteTime
            };
        }

        private static DateTime GetWriteTime(string path)
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
    }
}
