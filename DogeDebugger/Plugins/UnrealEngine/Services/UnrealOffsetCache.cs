using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;
using DogeDebugger.Core.Modules;
using DogeDebugger.Core.Process;
using DogeDebugger.Plugins.UnrealEngine.Models;

namespace DogeDebugger.Plugins.UnrealEngine.Services;

internal sealed class UnrealOffsetCache
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public UnrealOffsetCache(string? directory = null)
    {
        DirectoryPath = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DogeDebugger",
            "Saved",
            "UnrealEngine");
    }

    public string DirectoryPath { get; }

    public bool TryLoad(
        ModuleDescriptor module,
        ITargetProcess process,
        out UnrealOffsets? offsets,
        out string error)
    {
        offsets = null;
        error = string.Empty;
        try
        {
            string path = GetCachePath(module, process);
            if (!File.Exists(path))
            {
                error = "没有对应的缓存文件。";
                return false;
            }

            UnrealOffsetCacheDocument? document =
                JsonSerializer.Deserialize<UnrealOffsetCacheDocument>(
                    File.ReadAllText(path, Encoding.UTF8),
                    JsonOptions);
            if (document is null)
            {
                error = "缓存文件无法解析。";
                return false;
            }

            if (document.SchemaVersion != 1)
            {
                error =
                    $"缓存 schema 版本不匹配（文件 {document.SchemaVersion}，期望 1）。";
                return false;
            }

            if (document.ModuleSize != module.Size)
            {
                error = "模块大小与缓存记录不一致。";
                return false;
            }

            UnrealOffsets loaded = document.Offsets.Clone();
            loaded.NamePoolAddress = ResolveRva(document.NamePoolRva, module);
            loaded.NamePoolBlockArrayAddress =
                ResolveRva(document.NamePoolBlockArrayRva, module);
            loaded.GObjectsAddress = ResolveRva(document.GObjectsRva, module);
            loaded.GWorldAddress = ResolveRva(document.GWorldRva, module);
            loaded.GEngineAddress = ResolveRva(document.GEngineRva, module);
            loaded.NamePoolFirstBlockAddress = 0;
            if (loaded.NamePoolBlockArrayAddress != 0 &&
                process.TryReadBytes(
                    loaded.NamePoolBlockArrayAddress,
                    new byte[process.Is64Bit ? sizeof(ulong) : sizeof(uint)]))
            {
                byte[] pointerBytes = new byte[process.Is64Bit ? sizeof(ulong) : sizeof(uint)];
                if (process.TryReadBytes(loaded.NamePoolBlockArrayAddress, pointerBytes))
                {
                    loaded.NamePoolFirstBlockAddress = process.Is64Bit
                        ? BitConverter.ToUInt64(pointerBytes, 0)
                        : BitConverter.ToUInt32(pointerBytes, 0);
                }
            }

            offsets = loaded;
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            JsonException or
            NotSupportedException)
        {
            error = exception.Message;
            return false;
        }
    }

    public void Save(
        ModuleDescriptor module,
        UnrealOffsets offsets,
        ITargetProcess process)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            UnrealOffsets persisted = offsets.Clone();
            persisted.NamePoolAddress = 0;
            persisted.NamePoolBlockArrayAddress = 0;
            persisted.NamePoolFirstBlockAddress = 0;
            persisted.GObjectsAddress = 0;
            persisted.GWorldAddress = 0;
            persisted.GEngineAddress = 0;

            UnrealOffsetCacheDocument document = new()
            {
                ModulePath = module.FilePath,
                ModuleSize = module.Size,
                ImageHash = ComputeImageHash(module, process),
                CreatedUtc = DateTime.UtcNow,
                Offsets = persisted,
                NamePoolRva = ToRva(offsets.NamePoolAddress, module),
                NamePoolBlockArrayRva = ToRva(
                    offsets.NamePoolBlockArrayAddress,
                    module),
                GObjectsRva = ToRva(offsets.GObjectsAddress, module),
                GWorldRva = ToRva(offsets.GWorldAddress, module),
                GEngineRva = ToRva(offsets.GEngineAddress, module)
            };

            File.WriteAllText(
                GetCachePath(module, process),
                JsonSerializer.Serialize(document, JsonOptions),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            JsonException or
            NotSupportedException)
        {
        }
    }

    private string GetCachePath(ModuleDescriptor module, ITargetProcess process)
    {
        string moduleName = string.IsNullOrWhiteSpace(module.Name)
            ? Path.GetFileName(module.FilePath)
            : module.Name;
        string hash = ComputeImageHash(module, process);
        string fileName = $"{Sanitize(moduleName)}_{module.Size:X}_{hash}.offsets.json";
        return Path.Combine(DirectoryPath, fileName);
    }

    private static string ComputeImageHash(
        ModuleDescriptor module,
        ITargetProcess process)
    {
        byte[]? bytes = null;
        if (!string.IsNullOrWhiteSpace(module.FilePath) &&
            File.Exists(module.FilePath))
        {
            try
            {
                using FileStream stream = new(
                    module.FilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                bytes = new byte[Math.Min(1024, checked((int)Math.Min(stream.Length, 1024)))];
                int read = stream.Read(bytes, 0, bytes.Length);
                if (read > 0)
                {
                    bytes = bytes[..read];
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        bytes ??= process.IsOpen && module.BaseAddress != 0
            ? process.ReadBytes(module.BaseAddress, 1024)
            : [];
        return bytes.Length == 0
            ? "nohash"
            : Convert.ToHexString(SHA256.HashData(bytes).AsSpan(0, 16));
    }

    private static string Sanitize(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        return new string(value
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray());
    }

    private static ulong ResolveRva(uint? rva, ModuleDescriptor module) =>
        rva is > 0 ? module.BaseAddress + rva.Value : 0;

    private static uint? ToRva(ulong address, ModuleDescriptor module)
    {
        if (address == 0 || address < module.BaseAddress)
        {
            return null;
        }

        ulong rva = address - module.BaseAddress;
        return rva < module.Size ? checked((uint)rva) : null;
    }
}
