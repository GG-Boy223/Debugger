using System.IO;
using System.Text;

namespace DogeDebugger.Core.Cache;

public enum StaticAnalysisMachine : uint
{
    Unknown = 0,
    X86 = 0x014C,
    X64 = 0x8664
}

/// <summary>
/// Header of the original static-analysis cache payload
/// (<c>_analyze.bin</c>). Layout mirrors the original cache writer:
/// magic, schema version, module name, module path, base address, image size,
/// entry point, machine, file hash, analysis time and duration.
/// </summary>
public sealed class StaticAnalysisCacheHeader
{
    public const uint Magic = 0x43415344;

    public const ushort SchemaVersion = 2;

    private const int MaxShortStringLength = 32768;

    private const int MaxHashLength = 256;

    public required ushort Version { get; init; }

    public required string ModuleName { get; init; }

    public required string ModulePath { get; init; }

    public required ulong BaseAddress { get; init; }

    public required uint ImageSize { get; init; }

    public required uint EntryPointRva { get; init; }

    public required StaticAnalysisMachine Machine { get; init; }

    public required string FileHash { get; init; }

    public required DateTime AnalysisTime { get; init; }

    public required TimeSpan Duration { get; init; }

    public string ArchitectureText => Machine switch
    {
        StaticAnalysisMachine.X86 => "x86",
        StaticAnalysisMachine.X64 => "x64",
        _ => "未知架构"
    };

    public static bool TryRead(
        string? path,
        out StaticAnalysisCacheHeader? header,
        out string error)
    {
        header = null;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            error = "静态分析缓存文件不存在。";
            return false;
        }

        try
        {
            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                65536,
                FileOptions.SequentialScan);
            using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: false);
            uint magic = reader.ReadUInt32();
            if (magic != Magic)
            {
                error = "静态分析缓存文件头无效。";
                return false;
            }

            ushort version = reader.ReadUInt16();
            if (version != SchemaVersion)
            {
                error = $"不支持的静态分析缓存版本：{version}。";
                return false;
            }

            string moduleName = ReadString(reader, MaxShortStringLength);
            string modulePath = ReadString(reader, MaxShortStringLength);
            ulong baseAddress = reader.ReadUInt64();
            uint imageSize = reader.ReadUInt32();
            uint entryPointRva = reader.ReadUInt32();
            StaticAnalysisMachine machine = (StaticAnalysisMachine)reader.ReadUInt32();
            string fileHash = ReadString(reader, MaxHashLength);
            long analysisTimeBinary = reader.ReadInt64();
            long durationTicks = reader.ReadInt64();

            if (moduleName.Length == 0)
            {
                error = "静态分析缓存缺少模块名。";
                return false;
            }

            if (imageSize == 0)
            {
                error = "静态分析缓存中的镜像大小为零。";
                return false;
            }

            if (entryPointRva >= imageSize && entryPointRva != 0)
            {
                error = "静态分析缓存中的入口点超出了镜像范围。";
                return false;
            }

            if (machine is not StaticAnalysisMachine.X86 and not StaticAnalysisMachine.X64)
            {
                error = $"不支持的静态分析机器类型：0x{(uint)machine:X}。";
                return false;
            }

            if (fileHash.Length != 32 || !fileHash.All(Uri.IsHexDigit))
            {
                error = "静态分析缓存中的文件哈希不是 32 位十六进制。";
                return false;
            }

            header = new StaticAnalysisCacheHeader
            {
                Version = version,
                ModuleName = moduleName,
                ModulePath = modulePath,
                BaseAddress = baseAddress,
                ImageSize = imageSize,
                EntryPointRva = entryPointRva,
                Machine = machine,
                FileHash = fileHash.ToUpperInvariant(),
                AnalysisTime = DateTime.FromBinary(analysisTimeBinary),
                Duration = TimeSpan.FromTicks(durationTicks)
            };
            return true;
        }
        catch (Exception exception)
        {
            error = "读取静态分析缓存元数据失败：" + exception.Message;
            header = null;
            return false;
        }
    }

    private static string ReadString(BinaryReader reader, int maximumLength)
    {
        int length = Read7BitEncodedInt(reader);
        if (length < 0 || length > maximumLength)
        {
            throw new InvalidDataException(
                $"Serialized string length {length} exceeds the supported limit.");
        }

        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
        {
            throw new EndOfStreamException(
                "Unexpected end of file while reading a serialized string.");
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private static int Read7BitEncodedInt(BinaryReader reader)
    {
        uint result = 0;
        int shift = 0;
        while (true)
        {
            byte value = reader.ReadByte();
            result |= (uint)(value & 0x7F) << shift;
            if ((value & 0x80) == 0)
            {
                return (int)result;
            }

            shift += 7;
            if (shift >= 35)
            {
                throw new InvalidDataException("Invalid 7-bit encoded integer.");
            }
        }
    }
}
