using System.Runtime.InteropServices;
using DogeDebugger.Core.Native;
using DogeDebugger.Core.Process;

namespace DogeDebugger.Core.Modules;

public sealed class ModuleCatalog
{
    private static readonly int ModuleEntrySize =
        Marshal.SizeOf<NativeMethods.ModuleEntry32W>();

    public unsafe IReadOnlyList<ModuleDescriptor> Enumerate(ITargetProcess process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!process.IsOpen)
        {
            return [];
        }

        using SafeNativeHandle snapshot = new(
            NativeMethods.CreateToolhelp32Snapshot(
                NativeMethods.Th32csSnapshotModule,
                checked((uint)process.ProcessId)));

        if (snapshot.IsInvalid)
        {
            return [];
        }

        NativeMethods.ModuleEntry32W nativeEntry = new()
        {
            Size = checked((uint)ModuleEntrySize),
            ModuleName = string.Empty,
            ExecutablePath = string.Empty
        };

        List<ModuleDescriptor> modules = [];
        bool hasEntry = NativeMethods.Module32FirstW(snapshot.DangerousGetHandle(), ref nativeEntry);
        while (hasEntry)
        {
            ulong baseAddress = unchecked((ulong)nativeEntry.BaseAddress.ToInt64());
            ulong size = nativeEntry.BaseSize;
            ulong entryPoint = TryReadEntryPoint(process, baseAddress);
            modules.Add(new ModuleDescriptor
            {
                Name = nativeEntry.ModuleName,
                FilePath = nativeEntry.ExecutablePath,
                BaseAddress = baseAddress,
                Size = size,
                EntryPoint = entryPoint,
                ProcessId = checked((uint)process.ProcessId),
                IsMainModule = string.Equals(
                    nativeEntry.ExecutablePath,
                    process.FilePath,
                    StringComparison.OrdinalIgnoreCase)
            });

            nativeEntry.Size = checked((uint)ModuleEntrySize);
            hasEntry = NativeMethods.Module32NextW(snapshot.DangerousGetHandle(), ref nativeEntry);
        }

        return modules.ToArray();
    }

    public static ModuleDescriptor? FindByAddress(
        IEnumerable<ModuleDescriptor> modules,
        ulong address)
    {
        ModuleDescriptor? selected = null;
        foreach (ModuleDescriptor module in modules)
        {
            if (!module.Contains(address))
            {
                continue;
            }

            if (selected is null || module.Size < selected.Size)
            {
                selected = module;
            }
        }

        return selected;
    }

    private static ulong TryReadEntryPoint(ITargetProcess process, ulong baseAddress)
    {
        Span<byte> dosHeader = stackalloc byte[64];
        if (!process.TryReadBytes(baseAddress, dosHeader) ||
            dosHeader[0] != (byte)'M' ||
            dosHeader[1] != (byte)'Z')
        {
            return 0;
        }

        int peOffset = BitConverter.ToInt32(dosHeader[0x3C..0x40]);
        if (peOffset <= 0)
        {
            return 0;
        }

        Span<byte> peHeaders = stackalloc byte[256];
        if (!process.TryReadBytes(baseAddress + (uint)peOffset, peHeaders) ||
            peHeaders[0] != (byte)'P' ||
            peHeaders[1] != (byte)'E' ||
            peHeaders[2] != 0 ||
            peHeaders[3] != 0)
        {
            return 0;
        }

        ushort optionalHeaderMagic = BitConverter.ToUInt16(peHeaders[24..26]);
        int addressOfEntryPointOffset = optionalHeaderMagic switch
        {
            0x10B => 24 + 16,
            0x20B => 24 + 16,
            _ => -1
        };

        if (addressOfEntryPointOffset < 0)
        {
            return 0;
        }

        uint rva = BitConverter.ToUInt32(
            peHeaders.Slice(addressOfEntryPointOffset, sizeof(uint)));
        return rva == 0 ? 0 : baseAddress + rva;
    }
}
