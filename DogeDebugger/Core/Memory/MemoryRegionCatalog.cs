using DogeDebugger.Core.Modules;
using DogeDebugger.Core.Native;
using DogeDebugger.Core.Process;

namespace DogeDebugger.Core.Memory;

public sealed class MemoryRegionCatalog
{
    private const ulong MaximumUserAddress = 0x0000_7FFF_FFFF_FFFF;

    private readonly ModuleCatalog _moduleCatalog;

    public MemoryRegionCatalog(ModuleCatalog moduleCatalog)
    {
        _moduleCatalog = moduleCatalog;
    }

    public IReadOnlyList<MemoryRegionInfo> Enumerate(ITargetProcess process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!process.IsOpen)
        {
            return [];
        }

        IReadOnlyList<ModuleDescriptor> modules = _moduleCatalog.Enumerate(process);
        List<MemoryRegionInfo> regions = [];
        ulong address = 0;

        while (address < MaximumUserAddress)
        {
            UIntPtr result = NativeMethods.VirtualQueryEx(
                process.Handle,
                (UIntPtr)address,
                out NativeMethods.MemoryBasicInformation64 nativeRegion,
                (UIntPtr)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MemoryBasicInformation64>());

            if (result == UIntPtr.Zero || nativeRegion.RegionSize == 0)
            {
                break;
            }

            MemoryRegionInfo region = new()
            {
                BaseAddress = nativeRegion.BaseAddress,
                AllocationBase = nativeRegion.AllocationBase,
                Size = nativeRegion.RegionSize,
                State = nativeRegion.State,
                Type = nativeRegion.Type,
                AllocationProtect = nativeRegion.AllocationProtect,
                Protect = nativeRegion.Protect,
                AllocationProtectText = FormatProtection(nativeRegion.AllocationProtect),
                ProtectText = FormatProtection(nativeRegion.Protect)
            };

            ModuleDescriptor? module = FindModule(modules, region.BaseAddress);
            if (module is not null)
            {
                region.ModuleName = module.Name;
                region.IsModuleHeader = module.BaseAddress == region.BaseAddress;
            }

            regions.Add(region);

            ulong next = nativeRegion.BaseAddress + nativeRegion.RegionSize;
            if (next <= address)
            {
                break;
            }

            address = next;
        }

        return regions;
    }

    public static string FormatProtection(uint protection)
    {
        if (protection == 0)
        {
            return string.Empty;
        }

        string flags = string.Empty;
        if ((protection & NativeMethods.PageGuard) != 0)
        {
            flags += "G";
        }

        if ((protection & NativeMethods.PageNoCache) != 0)
        {
            flags += "N";
        }

        if ((protection & NativeMethods.PageWriteCombine) != 0)
        {
            flags += "W";
        }

        string access = (protection & 0xFF) switch
        {
            NativeMethods.PageNoAccess => "-",
            NativeMethods.PageReadOnly => "R",
            NativeMethods.PageReadWrite => "RW",
            NativeMethods.PageWriteCopy => "WC",
            NativeMethods.PageExecute => "X",
            NativeMethods.PageExecuteRead => "XR",
            NativeMethods.PageExecuteReadWrite => "XRW",
            NativeMethods.PageExecuteWriteCopy => "XWC",
            _ => "?"
        };

        return flags.Length == 0 ? access : $"{access} {flags}";
    }

    private static ModuleDescriptor? FindModule(
        IReadOnlyList<ModuleDescriptor> modules,
        ulong address)
    {
        foreach (ModuleDescriptor module in modules)
        {
            if (address >= module.BaseAddress && address < module.BaseAddress + module.Size)
            {
                return module;
            }
        }

        return null;
    }
}
