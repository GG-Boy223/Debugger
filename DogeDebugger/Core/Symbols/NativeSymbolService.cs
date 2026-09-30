using System.Runtime.InteropServices;
using System.IO;
using DogeDebugger.Core.Modules;
using DogeDebugger.Core.Process;

namespace DogeDebugger.Core.Symbols;

public sealed class NativeSymbolService : IDisposable
{
    private const uint SymOptionsLoadLines = 0x00000002;
    private const uint SymOptionsUndecorateName = 0x00000004;

    private readonly List<LoadedSymbolModule> _loadedModules = [];
    private IntPtr _processHandle;
    private bool _initialized;
    private bool _disposed;

    public IReadOnlyList<LoadedSymbolModule> LoadedModules => _loadedModules;

    public SymbolRefreshResult Refresh(
        ITargetProcess target,
        IReadOnlyList<ModuleDescriptor> modules,
        string? pdbRoot = null,
        string? sourceRoot = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(modules);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!target.IsOpen)
        {
            throw new InvalidOperationException("目标进程未打开");
        }

        if (_initialized && _processHandle != target.Handle)
        {
            Shutdown();
        }

        _processHandle = target.Handle;
        if (!_initialized)
        {
            string searchPath = BuildSearchPath(pdbRoot, sourceRoot);
            if (!NativeMethods.SymInitializeW(
                    _processHandle,
                    string.IsNullOrWhiteSpace(searchPath) ? null : searchPath,
                    false))
            {
                throw new InvalidOperationException(
                    $"SymInitializeW 失败: {Marshal.GetLastWin32Error()}");
            }

            NativeMethods.SymSetOptions(
                SymOptionsLoadLines | SymOptionsUndecorateName);
            _initialized = true;
        }

        NativeMethods.SymRefreshModuleList(_processHandle);
        _loadedModules.Clear();
        int loaded = 0;
        int failed = 0;
        foreach (ModuleDescriptor module in modules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (module.BaseAddress == 0 ||
                string.IsNullOrWhiteSpace(module.FilePath) ||
                !File.Exists(module.FilePath))
            {
                continue;
            }

            ulong moduleSize = Math.Min(module.Size, uint.MaxValue);
            ulong moduleHandle = NativeMethods.SymLoadModuleExW(
                _processHandle,
                IntPtr.Zero,
                module.FilePath,
                module.Name,
                module.BaseAddress,
                (uint)moduleSize,
                IntPtr.Zero,
                0);
            if (moduleHandle == 0)
            {
                failed++;
                continue;
            }

            loaded++;
            _loadedModules.Add(new LoadedSymbolModule(
                module.Name,
                module.FilePath,
                module.BaseAddress,
                moduleSize));
        }

        return new SymbolRefreshResult(
            loaded,
            failed,
            BuildSearchPath(pdbRoot, sourceRoot));
    }

    public bool TryGetLine(ulong address, out NativeSymbolLocation? location)
    {
        location = null;
        if (!_initialized || address == 0)
        {
            return false;
        }

        ImagehlpLine64 line = new()
        {
            SizeOfStruct = (uint)Marshal.SizeOf<ImagehlpLine64>()
        };
        if (!NativeMethods.SymGetLineFromAddrW64(_processHandle, address, out uint _, ref line))
        {
            return false;
        }

        string filePath = line.FileName == IntPtr.Zero
            ? string.Empty
            : Marshal.PtrToStringUni(line.FileName) ?? string.Empty;
        string functionName = TryGetFunctionName(address, out _);
        location = new NativeSymbolLocation(
            FindModuleName(address) ?? string.Empty,
            functionName,
            filePath,
            checked((int)line.LineNumber),
            line.Address,
            false);
        return true;
    }

    public bool TryGetFunction(ulong address, out NativeSymbolLocation? location)
    {
        location = null;
        string functionName = TryGetFunctionName(address, out ulong displacement);
        if (string.IsNullOrWhiteSpace(functionName))
        {
            return false;
        }

        location = new NativeSymbolLocation(
            FindModuleName(address) ?? string.Empty,
            functionName,
            string.Empty,
            0,
            address - displacement,
            false);
        return true;
    }

    public bool TryGetApproximateLine(
        ulong address,
        out NativeSymbolLocation? location,
        uint searchRadius = 4096)
    {
        location = null;
        if (!_initialized || address == 0)
        {
            return false;
        }

        if (TryGetLine(address, out location))
        {
            return true;
        }

        if (!TryGetFunction(address, out NativeSymbolLocation? function) ||
            function is null)
        {
            return false;
        }

        uint limit = Math.Min(searchRadius, 1024 * 1024);
        for (uint offset = 1; offset <= limit; offset += 4)
        {
            if (TryGetLine(function.Address + offset, out location) &&
                location is not null)
            {
                location = location with { IsApproximate = true };
                return true;
            }
        }

        location = function;
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Shutdown();
        _disposed = true;
    }

    private void Shutdown()
    {
        if (_initialized && _processHandle != IntPtr.Zero)
        {
            NativeMethods.SymCleanup(_processHandle);
        }

        _initialized = false;
        _processHandle = IntPtr.Zero;
        _loadedModules.Clear();
    }

    private string TryGetFunctionName(ulong address, out ulong displacement)
    {
        displacement = 0;
        if (!_initialized)
        {
            return string.Empty;
        }

        int bufferSize = 2048;
        IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            SymbolInfo symbol = new()
            {
                SizeOfStruct = (uint)Marshal.SizeOf<SymbolInfo>(),
                MaxNameLen = 1024
            };
            Marshal.StructureToPtr(symbol, buffer, false);
            if (!NativeMethods.SymFromAddrW(
                    _processHandle,
                    address,
                    out displacement,
                    buffer))
            {
                return string.Empty;
            }

            symbol = Marshal.PtrToStructure<SymbolInfo>(buffer);
            int nameOffset = Marshal.OffsetOf<SymbolInfo>(nameof(SymbolInfo.Name)).ToInt32();
            return Marshal.PtrToStringUni(
                IntPtr.Add(buffer, nameOffset),
                checked((int)symbol.NameLen)) ?? string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private string? FindModuleName(ulong address) =>
        _loadedModules
            .FirstOrDefault(module =>
                address >= module.BaseAddress &&
                address < module.BaseAddress + module.Size)
            ?.Name;

    private static string BuildSearchPath(string? pdbRoot, string? sourceRoot)
    {
        List<string> paths = [];
        if (!string.IsNullOrWhiteSpace(pdbRoot))
        {
            paths.Add(pdbRoot);
        }

        if (!string.IsNullOrWhiteSpace(sourceRoot))
        {
            paths.Add(sourceRoot);
        }

        paths.Add("srv*");
        return string.Join(';', paths.Distinct(StringComparer.OrdinalIgnoreCase));
    }
}

public sealed record LoadedSymbolModule(
    string Name,
    string FilePath,
    ulong BaseAddress,
    ulong Size);

public sealed record SymbolRefreshResult(
    int LoadedModuleCount,
    int FailedModuleCount,
    string SearchPath);

public sealed record NativeSymbolLocation(
    string ModuleName,
    string FunctionName,
    string FilePath,
    int Line,
    ulong Address,
    bool IsApproximate);

[StructLayout(LayoutKind.Sequential)]
internal struct ImagehlpLine64
{
    public uint SizeOfStruct;
    public IntPtr Key;
    public uint LineNumber;
    public IntPtr FileName;
    public ulong Address;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct SymbolInfo
{
    public uint SizeOfStruct;
    public uint TypeIndex;
    public ulong Reserved;
    public uint Index;
    public uint Size;
    public ulong ModBase;
    public uint Flags;
    public ulong Value;
    public ulong Address;
    public uint Register;
    public uint Scope;
    public uint Tag;
    public uint NameLen;
    public uint MaxNameLen;
    public char Name;
}

internal static class NativeMethods
{
    private const string DbgHelp = "dbghelp.dll";

    [DllImport(DbgHelp, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SymInitializeW(
        IntPtr process,
        string? userSearchPath,
        [MarshalAs(UnmanagedType.Bool)] bool invadeProcess);

    [DllImport(DbgHelp, SetLastError = true)]
    public static extern uint SymSetOptions(uint options);

    [DllImport(DbgHelp, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ulong SymLoadModuleExW(
        IntPtr process,
        IntPtr fileHandle,
        string imageName,
        string? moduleName,
        ulong baseAddress,
        uint dllSize,
        IntPtr data,
        uint flags);

    [DllImport(DbgHelp, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SymGetLineFromAddrW64(
        IntPtr process,
        ulong address,
        out uint displacement,
        ref ImagehlpLine64 line);

    [DllImport(DbgHelp, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SymFromAddrW(
        IntPtr process,
        ulong address,
        out ulong displacement,
        IntPtr symbol);

    [DllImport(DbgHelp, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SymRefreshModuleList(IntPtr process);

    [DllImport(DbgHelp, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SymCleanup(IntPtr process);
}
