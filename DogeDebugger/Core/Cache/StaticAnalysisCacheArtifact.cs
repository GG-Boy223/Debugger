using System.IO;

namespace DogeDebugger.Core.Cache;

/// <summary>
/// A paired <c>_analyze.bin</c> / <c>_dump.bin</c> artifact discovered in
/// the managed analysis tree. Mirrors the original artifact UI model.
/// </summary>
public sealed class StaticAnalysisCacheArtifact
{
    public required string ArtifactId { get; init; }

    public required string ModuleName { get; init; }

    public required string FileHash { get; init; }

    public string? AnalyzePath { get; init; }

    public string? DumpPath { get; init; }

    public StaticAnalysisCacheHeader? Header { get; init; }

    public required DateTime LastWriteTime { get; init; }

    public string Architecture => Header?.ArchitectureText ?? "未知架构";

    public ulong ImageSize => Header?.ImageSize ?? 0;

    public string ShortHash => FileHash.Length <= 10 ? FileHash : FileHash[..10];

    public bool IsPaired => AnalyzePath is not null && DumpPath is not null && Header is not null;

    public string StatusText
    {
        get
        {
            if (AnalyzePath is not null && DumpPath is not null)
            {
                return Header is null
                    ? "分析缓存无效或无法读取"
                    : "缓存配对完整";
            }

            return AnalyzePath is null
                ? "缺少静态分析文件"
                : "缺少模块转储文件";
        }
    }

    public string DisplayName =>
        $"{ModuleName}  [{ShortHash}]" + (IsPaired ? string.Empty : "  · 不完整");

    public string MetadataText =>
        $"{Architecture}  ·  镜像大小 0x{ImageSize:X}  ·  哈希 {ShortHash}";

    public string? ResolveModulePath()
    {
        string? path = Header?.ModulePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return File.Exists(path) ? Path.GetFullPath(path) : null;
        }
        catch
        {
            return null;
        }
    }
}
