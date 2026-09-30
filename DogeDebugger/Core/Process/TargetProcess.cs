using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using DogeDebugger.Core.Modules;
using DogeDebugger.Core.Native;

namespace DogeDebugger.Core.Process;

public sealed class TargetProcess : ITargetProcess
{
    private const uint Access =
        NativeMethods.ProcessQueryInformation |
        NativeMethods.ProcessVirtualMemoryRead |
        NativeMethods.ProcessVirtualMemoryWrite |
        NativeMethods.ProcessVirtualMemoryOperation |
        NativeMethods.ProcessCreateThread |
        NativeMethods.ProcessSuspendResume;

    private SafeNativeHandle? _handle;
    private System.Diagnostics.Process? _process;
    private bool _hasMemory;
    private bool _isDebugging;
    private bool _disposed;

    public int ProcessId { get; private set; }

    public string ProcessName { get; private set; } = string.Empty;

    public string FilePath { get; private set; } = string.Empty;

    public IntPtr Handle => _handle is { IsInvalid: false } handle ? handle.DangerousGetHandle() : IntPtr.Zero;

    public bool IsOpen => Handle != IntPtr.Zero;

    public bool Is64Bit { get; private set; }

    public bool HasMemory => _hasMemory;

    public bool IsDebugging => _isDebugging;

    public event PropertyChangedEventHandler? PropertyChanged;

    public event Action<ITargetProcess>? Opened;

    public event Action? Closed;

    public event Action? DebuggerAttached;

    public event Action? DebuggerDetached;

    public bool Open(int processId, string processName, string filePath)
    {
        ThrowIfDisposed();
        Close();

        IntPtr rawHandle = NativeMethods.OpenProcess(Access, inheritHandle: false, processId);
        if (rawHandle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            _process = System.Diagnostics.Process.GetProcessById(processId);
            _handle = new SafeNativeHandle(rawHandle);
            ProcessId = processId;
            ProcessName = string.IsNullOrWhiteSpace(processName)
                ? _process.ProcessName
                : processName;
            FilePath = string.IsNullOrWhiteSpace(filePath)
                ? QueryProcessPath(rawHandle) ?? string.Empty
                : filePath;
            Is64Bit = !NativeMethods.IsWow64Process(rawHandle, out bool isWow64) || !isWow64;
            _hasMemory = true;
            NotifyStateChanged(nameof(IsOpen));
            Opened?.Invoke(this);
            return true;
        }
        catch
        {
            NativeMethods.CloseHandle(rawHandle);
            _process?.Dispose();
            _process = null;
            _handle = null;
            throw;
        }
    }

    public bool OpenFromHandle(
        int processId,
        string processName,
        string filePath,
        IntPtr processHandle)
    {
        ThrowIfDisposed();
        if (processHandle == IntPtr.Zero)
        {
            return false;
        }

        Close();
        IntPtr ownedHandle = NativeMethods.OpenProcess(Access, inheritHandle: false, processId);
        if (ownedHandle == IntPtr.Zero)
        {
            ownedHandle = processHandle;
        }
        else
        {
            NativeMethods.CloseHandle(processHandle);
        }

        _process = System.Diagnostics.Process.GetProcessById(processId);
        _handle = new SafeNativeHandle(ownedHandle);
        ProcessId = processId;
        ProcessName = string.IsNullOrWhiteSpace(processName)
            ? _process.ProcessName
            : processName;
        FilePath = string.IsNullOrWhiteSpace(filePath)
            ? QueryProcessPath(ownedHandle) ?? string.Empty
            : filePath;
        Is64Bit = !NativeMethods.IsWow64Process(ownedHandle, out bool isWow64) || !isWow64;
        _hasMemory = true;
        NotifyStateChanged(nameof(IsOpen));
        Opened?.Invoke(this);
        return true;
    }

    public void Close()
    {
        if (!IsOpen)
        {
            return;
        }

        _handle?.Dispose();
        _handle = null;
        _process?.Dispose();
        _process = null;
        _hasMemory = false;
        _isDebugging = false;
        ProcessId = 0;
        ProcessName = string.Empty;
        FilePath = string.Empty;
        NotifyStateChanged(nameof(IsOpen));
        Closed?.Invoke();
    }

    public unsafe bool TryReadBytes(ulong address, Span<byte> buffer)
    {
        ThrowIfDisposed();
        if (!IsOpen || address == 0)
        {
            return false;
        }

        if (buffer.Length == 0)
        {
            return true;
        }

        fixed (byte* destination = buffer)
        {
            bool succeeded = NativeMethods.ReadProcessMemory(
                Handle,
                (void*)(nuint)address,
                destination,
                (nuint)buffer.Length,
                out UIntPtr bytesRead);
            return succeeded && bytesRead == (nuint)buffer.Length;
        }
    }

    public byte[] ReadBytes(ulong address, int size)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        if (size == 0)
        {
            return [];
        }

        byte[] buffer = new byte[size];
        int read = ReadBytesPartial(address, buffer);
        if (read == size)
        {
            return buffer;
        }

        Array.Resize(ref buffer, read);
        return buffer;
    }

    public unsafe int ReadBytesPartial(ulong address, Span<byte> buffer)
    {
        ThrowIfDisposed();
        if (!IsOpen || address == 0 || buffer.Length == 0)
        {
            return 0;
        }

        fixed (byte* destination = buffer)
        {
            return NativeMethods.ReadProcessMemory(
                Handle,
                (void*)(nuint)address,
                destination,
                (nuint)buffer.Length,
                out UIntPtr bytesRead)
                ? checked((int)bytesRead)
                : 0;
        }
    }

    public unsafe bool TryWriteBytes(ulong address, ReadOnlySpan<byte> bytes)
    {
        ThrowIfDisposed();
        if (!IsOpen || address == 0 || bytes.Length == 0)
        {
            return bytes.Length == 0;
        }

        fixed (byte* source = bytes)
        {
            return NativeMethods.WriteProcessMemory(
                Handle,
                (void*)(nuint)address,
                source,
                (nuint)bytes.Length,
                out UIntPtr bytesWritten) &&
                bytesWritten == (nuint)bytes.Length;
        }
    }

    public void MarkDebuggerAttached()
    {
        if (_isDebugging)
        {
            return;
        }

        _isDebugging = true;
        NotifyStateChanged(nameof(IsDebugging));
        DebuggerAttached?.Invoke();
    }

    public ulong AllocateMemory(
        ulong preferredAddress,
        int size,
        uint protection)
    {
        ThrowIfDisposed();
        if (!IsOpen || size <= 0)
        {
            return 0;
        }

        IntPtr address = NativeMethods.VirtualAllocEx(
            Handle,
            unchecked((IntPtr)(nint)preferredAddress),
            (nuint)size,
            NativeMethods.MemCommit | NativeMethods.MemReserve,
            protection);
        return address == IntPtr.Zero ? 0 : unchecked((ulong)address.ToInt64());
    }

    public bool FreeMemory(ulong address)
    {
        ThrowIfDisposed();
        return IsOpen &&
               address != 0 &&
               NativeMethods.VirtualFreeEx(
                   Handle,
                   unchecked((IntPtr)(nint)address),
                   UIntPtr.Zero,
                   NativeMethods.MemRelease);
    }

    public int CreateRemoteThread(
        ulong startAddress,
        ulong parameter,
        uint creationFlags)
    {
        ThrowIfDisposed();
        if (!IsOpen || startAddress == 0)
        {
            return 0;
        }

        IntPtr threadHandle = NativeMethods.CreateRemoteThread(
            Handle,
            IntPtr.Zero,
            UIntPtr.Zero,
            unchecked((IntPtr)(nint)startAddress),
            unchecked((IntPtr)(nint)parameter),
            creationFlags,
            out uint threadId);
        if (threadHandle == IntPtr.Zero)
        {
            return 0;
        }

        NativeMethods.CloseHandle(threadHandle);
        return unchecked((int)threadId);
    }

    public uint ExecuteRemoteThreadAndWait(
        ulong startAddress,
        ulong parameter,
        uint creationFlags = 0)
    {
        return TryExecuteRemoteThreadAndWait(
            startAddress,
            parameter,
            creationFlags,
            NativeMethods.Infinite,
            out uint exitCode)
                ? exitCode
                : 0;
    }

    public uint CallRemoteFunction(ulong functionAddress, IReadOnlyList<ulong> arguments) =>
        unchecked((uint)CallRemoteFunction64(functionAddress, arguments).ReturnValue);

    public RemoteOperationResult CallRemoteFunction64(
        ulong functionAddress,
        IReadOnlyList<ulong> arguments,
        int timeoutMilliseconds = 5000,
        uint creationFlags = 0)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(arguments);
        Stopwatch stopwatch = Stopwatch.StartNew();
        if (!IsOpen || functionAddress == 0)
        {
            return RemoteOperationResult.Failure("目标进程未打开或调用地址无效。", stopwatch.Elapsed);
        }

        if (!Is64Bit)
        {
            return RemoteOperationResult.Failure("Call 函数仅支持 64 位进程。", stopwatch.Elapsed);
        }

        if (arguments.Count > 8)
        {
            return RemoteOperationResult.Failure(
                "Call 函数最多支持 8 个参数。",
                stopwatch.Elapsed);
        }

        const int allocationSize = 4096;
        ulong allocation = AllocateMemory(
            0,
            allocationSize,
            NativeMethods.PageExecuteReadWrite);
        if (allocation == 0)
        {
            return RemoteOperationResult.Failure(
                BuildWin32Error("无法分配远程内存"),
                stopwatch.Elapsed);
        }

        try
        {
            ulong resultAddress = allocation + 512;
            byte[] stub = BuildX64CallStub(
                functionAddress,
                arguments,
                resultAddress);
            if (!TryWriteBytes(allocation, stub))
            {
                return RemoteOperationResult.Failure(
                    BuildWin32Error("无法写入远程内存"),
                    stopwatch.Elapsed);
            }

            if (!TryExecuteRemoteThreadAndWait(
                    allocation,
                    0,
                    creationFlags,
                    unchecked((uint)Math.Max(1, timeoutMilliseconds)),
                    out _))
            {
                return RemoteOperationResult.Failure(
                    "远程调用线程未在超时时间内结束。",
                    stopwatch.Elapsed);
            }

            Span<byte> resultBytes = stackalloc byte[sizeof(ulong)];
            if (!TryReadBytes(resultAddress, resultBytes))
            {
                return RemoteOperationResult.Failure(
                    BuildWin32Error("无法读取远程调用结果"),
                    stopwatch.Elapsed);
            }

            return RemoteOperationResult.Success(
                BitConverter.ToUInt64(resultBytes),
                stopwatch.Elapsed);
        }
        finally
        {
            FreeMemory(allocation);
        }
    }

    public ulong InjectDll(string path) =>
        InjectDll(path, null).ReturnValue;

    public RemoteOperationResult InjectDll(
        string path,
        string? exportName,
        int timeoutMilliseconds = 5000)
    {
        ThrowIfDisposed();
        Stopwatch stopwatch = Stopwatch.StartNew();
        if (!IsOpen)
        {
            return RemoteOperationResult.Failure("目标进程未打开。", stopwatch.Elapsed);
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return RemoteOperationResult.Failure("DLL 路径为空。", stopwatch.Elapsed);
        }

        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            return RemoteOperationResult.Failure("DLL 文件不存在。", stopwatch.Elapsed);
        }

        if (!TryResolveRemoteExport(
                "kernel32.dll",
                "LoadLibraryW",
                out ulong loadLibraryAddress,
                out string resolveError))
        {
            return RemoteOperationResult.Failure(resolveError, stopwatch.Elapsed);
        }

        ulong getProcAddressAddress = 0;
        if (!string.IsNullOrWhiteSpace(exportName) &&
            !TryResolveRemoteExport(
                "kernel32.dll",
                "GetProcAddress",
                out getProcAddressAddress,
                out resolveError))
        {
            return RemoteOperationResult.Failure(resolveError, stopwatch.Elapsed);
        }

        byte[] pathBytes = Encoding.Unicode.GetBytes(fullPath + '\0');
        byte[]? exportBytes = string.IsNullOrWhiteSpace(exportName)
            ? null
            : Encoding.ASCII.GetBytes(exportName.Trim() + '\0');

        const int allocationSize = 4096;
        ulong pathAddress = AllocateMemory(
            0,
            Math.Max(pathBytes.Length, 2),
            NativeMethods.PageReadWrite);
        ulong exportAddress = exportBytes is null
            ? 0
            : AllocateMemory(
                0,
                exportBytes.Length,
                NativeMethods.PageReadWrite);
        ulong stubAddress = AllocateMemory(
            0,
            allocationSize,
            NativeMethods.PageExecuteReadWrite);

        if (pathAddress == 0 ||
            (exportBytes is not null && exportAddress == 0) ||
            stubAddress == 0)
        {
            FreeRemoteAllocation(stubAddress);
            FreeRemoteAllocation(exportAddress);
            FreeRemoteAllocation(pathAddress);
            return RemoteOperationResult.Failure(
                BuildWin32Error("无法分配远程注入内存"),
                stopwatch.Elapsed);
        }

        try
        {
            ulong resultAddress = stubAddress + 1024;
            byte[] stub = BuildRemoteInjectStub(
                Is64Bit,
                pathAddress,
                loadLibraryAddress,
                exportAddress,
                getProcAddressAddress,
                resultAddress);

            if (!TryWriteBytes(pathAddress, pathBytes) ||
                (exportBytes is not null && !TryWriteBytes(exportAddress, exportBytes)) ||
                !TryWriteBytes(stubAddress, stub))
            {
                return RemoteOperationResult.Failure(
                    BuildWin32Error("无法写入远程注入内存"),
                    stopwatch.Elapsed);
            }

            if (!TryExecuteRemoteThreadAndWait(
                    stubAddress,
                    0,
                    0,
                    unchecked((uint)Math.Max(1, timeoutMilliseconds)),
                    out uint exitCode) ||
                exitCode == 0)
            {
                return RemoteOperationResult.Failure(
                    "DLL 注入线程执行失败。",
                    stopwatch.Elapsed);
            }

            Span<byte> resultBytes = stackalloc byte[sizeof(ulong)];
            if (!TryReadBytes(resultAddress, resultBytes))
            {
                return RemoteOperationResult.Failure(
                    BuildWin32Error("无法读取远程注入结果"),
                    stopwatch.Elapsed);
            }

            return RemoteOperationResult.Success(
                BitConverter.ToUInt64(resultBytes),
                stopwatch.Elapsed);
        }
        finally
        {
            FreeRemoteAllocation(stubAddress);
            FreeRemoteAllocation(exportAddress);
            FreeRemoteAllocation(pathAddress);
        }
    }

    public RemoteOperationResult UnloadDll(
        string path,
        int timeoutMilliseconds = 5000)
    {
        ThrowIfDisposed();
        Stopwatch stopwatch = Stopwatch.StartNew();
        if (!IsOpen)
        {
            return RemoteOperationResult.Failure("目标进程未打开。", stopwatch.Elapsed);
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return RemoteOperationResult.Failure("DLL 路径为空。", stopwatch.Elapsed);
        }

        if (!TryResolveRemoteExport(
                "kernel32.dll",
                "GetModuleHandleW",
                out ulong getModuleHandleAddress,
                out string resolveError) ||
            !TryResolveRemoteExport(
                "kernel32.dll",
                "FreeLibrary",
                out ulong freeLibraryAddress,
                out resolveError))
        {
            return RemoteOperationResult.Failure(resolveError, stopwatch.Elapsed);
        }

        byte[] pathBytes = Encoding.Unicode.GetBytes(Path.GetFullPath(path) + '\0');
        ulong pathAddress = AllocateMemory(
            0,
            pathBytes.Length,
            NativeMethods.PageReadWrite);
        ulong stubAddress = AllocateMemory(
            0,
            4096,
            NativeMethods.PageExecuteReadWrite);
        if (pathAddress == 0 || stubAddress == 0)
        {
            FreeRemoteAllocation(stubAddress);
            FreeRemoteAllocation(pathAddress);
            return RemoteOperationResult.Failure(
                BuildWin32Error("无法分配远程卸载内存"),
                stopwatch.Elapsed);
        }

        try
        {
            ulong resultAddress = stubAddress + 1024;
            byte[] stub = BuildRemoteUnloadStub(
                Is64Bit,
                pathAddress,
                getModuleHandleAddress,
                freeLibraryAddress,
                resultAddress);
            if (!TryWriteBytes(pathAddress, pathBytes) ||
                !TryWriteBytes(stubAddress, stub))
            {
                return RemoteOperationResult.Failure(
                    BuildWin32Error("无法写入远程卸载内存"),
                    stopwatch.Elapsed);
            }

            if (!TryExecuteRemoteThreadAndWait(
                    stubAddress,
                    0,
                    0,
                    unchecked((uint)Math.Max(1, timeoutMilliseconds)),
                    out uint exitCode) ||
                exitCode == 0)
            {
                return RemoteOperationResult.Failure(
                    "DLL 卸载线程执行失败。",
                    stopwatch.Elapsed);
            }

            Span<byte> resultBytes = stackalloc byte[sizeof(ulong)];
            if (!TryReadBytes(resultAddress, resultBytes))
            {
                return RemoteOperationResult.Failure(
                    BuildWin32Error("无法读取远程卸载结果"),
                    stopwatch.Elapsed);
            }

            return RemoteOperationResult.Success(
                BitConverter.ToUInt64(resultBytes),
                stopwatch.Elapsed);
        }
        finally
        {
            FreeRemoteAllocation(stubAddress);
            FreeRemoteAllocation(pathAddress);
        }
    }

    public void MarkDebuggerDetached()
    {
        if (!_isDebugging)
        {
            return;
        }

        _isDebugging = false;
        NotifyStateChanged(nameof(IsDebugging));
        DebuggerDetached?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Close();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private static string? QueryProcessPath(IntPtr handle)
    {
        char[] path = new char[32768];
        uint length = (uint)path.Length;
        return NativeMethods.QueryFullProcessImageNameW(handle, 0, path, ref length)
            ? new string(path, 0, checked((int)length))
            : null;
    }

    internal bool TryExecuteRemoteThreadAndWait(
        ulong startAddress,
        ulong parameter,
        uint creationFlags,
        uint timeoutMilliseconds,
        out uint exitCode)
    {
        exitCode = 0;
        IntPtr threadHandle = NativeMethods.CreateRemoteThread(
            Handle,
            IntPtr.Zero,
            UIntPtr.Zero,
            unchecked((IntPtr)(nint)startAddress),
            unchecked((IntPtr)(nint)parameter),
            creationFlags,
            out _);
        if (threadHandle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            uint waitResult = NativeMethods.WaitForSingleObject(
                threadHandle,
                timeoutMilliseconds);
            if (waitResult != NativeMethods.WaitObject0)
            {
                return false;
            }

            return NativeMethods.GetExitCodeThread(threadHandle, out exitCode);
        }
        finally
        {
            NativeMethods.CloseHandle(threadHandle);
        }
    }

    private bool TryResolveRemoteExport(
        string moduleName,
        string exportName,
        out ulong address,
        out string errorMessage)
    {
        address = 0;
        errorMessage = string.Empty;
        if (Environment.Is64BitProcess == Is64Bit)
        {
            IntPtr localModule = NativeMethods.GetModuleHandleW(moduleName);
            if (localModule != IntPtr.Zero)
            {
                IntPtr localExport = NativeMethods.GetProcAddress(localModule, exportName);
                if (localExport != IntPtr.Zero)
                {
                    address = unchecked((ulong)localExport.ToInt64());
                    return true;
                }
            }
        }

        ModuleDescriptor? module = new ModuleCatalog()
            .Enumerate(this)
            .FirstOrDefault(candidate => string.Equals(
                candidate.Name,
                moduleName,
                StringComparison.OrdinalIgnoreCase));
        if (module is null)
        {
            errorMessage = $"目标进程中未找到 {moduleName}。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(module.FilePath) ||
            !File.Exists(module.FilePath))
        {
            errorMessage = $"无法读取 {moduleName} 的磁盘模块。";
            return false;
        }

        PeExport? export;
        try
        {
            export = new PeModuleAnalyzer()
                .AnalyzeFile(module.FilePath)
                .Exports
                .FirstOrDefault(candidate => string.Equals(
                    candidate.Name,
                    exportName,
                    StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception)
        {
            errorMessage = $"解析 {moduleName} 导出失败：{exception.Message}";
            return false;
        }

        if (export is null)
        {
            errorMessage = $"{moduleName} 中未找到导出 {exportName}。";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(export.ForwarderName))
        {
            int separator = export.ForwarderName.LastIndexOf('.');
            if (separator <= 0 || separator >= export.ForwarderName.Length - 1)
            {
                errorMessage = $"无法解析转发导出 {export.ForwarderName}。";
                return false;
            }

            string forwardedModule = export.ForwarderName[..separator];
            if (!forwardedModule.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                forwardedModule += ".dll";
            }

            return TryResolveRemoteExport(
                forwardedModule,
                export.ForwarderName[(separator + 1)..],
                out address,
                out errorMessage);
        }

        address = checked(module.BaseAddress + export.FunctionRva);
        return address != 0;
    }

    private static byte[] BuildX64CallStub(
        ulong functionAddress,
        IReadOnlyList<ulong> arguments,
        ulong resultAddress)
    {
        List<byte> code =
        [
            0x48, 0x83, 0xEC, 0x68,
            0x48, 0xB9,
            .. BitConverter.GetBytes(arguments.Count > 0 ? arguments[0] : 0),
            0x48, 0xBA,
            .. BitConverter.GetBytes(arguments.Count > 1 ? arguments[1] : 0),
            0x49, 0xB8,
            .. BitConverter.GetBytes(arguments.Count > 2 ? arguments[2] : 0),
            0x49, 0xB9,
            .. BitConverter.GetBytes(arguments.Count > 3 ? arguments[3] : 0),
        ];

        AddX64StackArgument(code, 4, 0x20, arguments.Count > 4 ? arguments[4] : 0);
        AddX64StackArgument(code, 5, 0x28, arguments.Count > 5 ? arguments[5] : 0);
        AddX64StackArgument(code, 6, 0x30, arguments.Count > 6 ? arguments[6] : 0);
        AddX64StackArgument(code, 7, 0x38, arguments.Count > 7 ? arguments[7] : 0);
        code.AddRange(
        [
            0x48, 0xB8,
            .. BitConverter.GetBytes(functionAddress),
            0xFF, 0xD0,
            0x48, 0xB9,
            .. BitConverter.GetBytes(resultAddress),
            0x48, 0x89, 0x01,
            0x48, 0x83, 0xC4, 0x68,
            0xC3
        ]);
        return code.ToArray();
    }

    private static void AddX64StackArgument(
        List<byte> code,
        int argumentIndex,
        byte stackOffset,
        ulong value)
    {
        _ = argumentIndex;
        code.AddRange(
        [
            0x49, 0xBA,
            .. BitConverter.GetBytes(value),
            0x4C, 0x89, 0x54, 0x24, stackOffset
        ]);
    }

    private static byte[] BuildRemoteInjectStub(
        bool is64Bit,
        ulong pathAddress,
        ulong loadLibraryAddress,
        ulong exportNameAddress,
        ulong getProcAddressAddress,
        ulong resultAddress)
    {
        return is64Bit
            ? BuildX64InjectStub(
                pathAddress,
                loadLibraryAddress,
                exportNameAddress,
                getProcAddressAddress,
                resultAddress)
            : BuildX86InjectStub(
                pathAddress,
                loadLibraryAddress,
                exportNameAddress,
                getProcAddressAddress,
                resultAddress);
    }

    private static byte[] BuildX64InjectStub(
        ulong pathAddress,
        ulong loadLibraryAddress,
        ulong exportNameAddress,
        ulong getProcAddressAddress,
        ulong resultAddress)
    {
        List<byte> code =
        [
            0x48, 0x83, 0xEC, 0x28,
            0x48, 0xB9,
            .. BitConverter.GetBytes(pathAddress),
            0x48, 0xB8,
            .. BitConverter.GetBytes(loadLibraryAddress),
            0xFF, 0xD0,
            0x48, 0x85, 0xC0
        ];
        int loadLibraryFailureJump = AddJump(code, 0x74);
        code.AddRange(
        [
            0x48, 0xB9,
            .. BitConverter.GetBytes(resultAddress),
            0x48, 0x89, 0x01
        ]);

        if (exportNameAddress != 0)
        {
            code.AddRange(
            [
                0x49, 0x89, 0xC2,
                0x4C, 0x89, 0xD1,
                0x48, 0xBA,
                .. BitConverter.GetBytes(exportNameAddress),
                0x48, 0xB8,
                .. BitConverter.GetBytes(getProcAddressAddress),
                0xFF, 0xD0,
                0x48, 0x85, 0xC0
            ]);
            int getProcAddressFailureJump = AddJump(code, 0x74);
            code.AddRange(
            [
                0xFF, 0xD0,
                0x48, 0xB9,
                .. BitConverter.GetBytes(resultAddress),
                0x48, 0x89, 0x01
            ]);
            code.AddRange(
            [
                0xB8, 0x01, 0x00, 0x00, 0x00,
                0x48, 0x83, 0xC4, 0x28,
                0xC3
            ]);
            int failure = code.Count;
            PatchJump(code, loadLibraryFailureJump, failure);
            PatchJump(code, getProcAddressFailureJump, failure);
        }
        else
        {
            code.AddRange(
            [
                0xB8, 0x01, 0x00, 0x00, 0x00,
                0x48, 0x83, 0xC4, 0x28,
                0xC3
            ]);
            int failure = code.Count;
            PatchJump(code, loadLibraryFailureJump, failure);
        }

        code.AddRange(
        [
            0x48, 0xB9,
            .. BitConverter.GetBytes(resultAddress),
            0x31, 0xC0,
            0x48, 0x89, 0x01,
            0x48, 0x83, 0xC4, 0x28,
            0xC3
        ]);
        return code.ToArray();
    }

    private static byte[] BuildX86InjectStub(
        ulong pathAddress,
        ulong loadLibraryAddress,
        ulong exportNameAddress,
        ulong getProcAddressAddress,
        ulong resultAddress)
    {
        uint path = checked((uint)pathAddress);
        uint loadLibrary = checked((uint)loadLibraryAddress);
        uint exportName = checked((uint)exportNameAddress);
        uint getProcAddress = checked((uint)getProcAddressAddress);
        uint result = checked((uint)resultAddress);

        List<byte> code =
        [
            0x68,
            .. BitConverter.GetBytes(path),
            0xB8,
            .. BitConverter.GetBytes(loadLibrary),
            0xFF, 0xD0,
            0x85, 0xC0
        ];
        int loadLibraryFailureJump = AddJump(code, 0x74);

        if (exportNameAddress != 0)
        {
            code.AddRange(
            [
                0x50,
                0x68,
                .. BitConverter.GetBytes(exportName),
                0x50,
                0xB8,
                .. BitConverter.GetBytes(getProcAddress),
                0xFF, 0xD0,
                0x85, 0xC0
            ]);
            int getProcAddressFailureJump = AddJump(code, 0x74);
            code.AddRange(
            [
                0xFF, 0xD0,
                0xA3,
                .. BitConverter.GetBytes(result),
                0x59,
                0xB8, 0x01, 0x00, 0x00, 0x00,
                0xC3
            ]);

            int failureWithModule = code.Count;
            code.Add(0x59);
            int failure = code.Count;
            PatchJump(code, loadLibraryFailureJump, failure);
            PatchJump(code, getProcAddressFailureJump, failureWithModule);
            code.AddRange(
            [
                0xC7, 0x05,
                .. BitConverter.GetBytes(result),
                0x00, 0x00, 0x00, 0x00,
                0x31, 0xC0,
                0xC3
            ]);
            return code.ToArray();
        }

        code.AddRange(
        [
            0xA3,
            .. BitConverter.GetBytes(result),
            0xB8, 0x01, 0x00, 0x00, 0x00,
            0xC3
        ]);
        int fail = code.Count;
        PatchJump(code, loadLibraryFailureJump, fail);
        code.AddRange(
        [
            0xC7, 0x05,
            .. BitConverter.GetBytes(result),
            0x00, 0x00, 0x00, 0x00,
            0x31, 0xC0,
            0xC3
        ]);
        return code.ToArray();
    }

    private static byte[] BuildRemoteUnloadStub(
        bool is64Bit,
        ulong pathAddress,
        ulong getModuleHandleAddress,
        ulong freeLibraryAddress,
        ulong resultAddress)
    {
        return is64Bit
            ? BuildX64UnloadStub(
                pathAddress,
                getModuleHandleAddress,
                freeLibraryAddress,
                resultAddress)
            : BuildX86UnloadStub(
                pathAddress,
                getModuleHandleAddress,
                freeLibraryAddress,
                resultAddress);
    }

    private static byte[] BuildX64UnloadStub(
        ulong pathAddress,
        ulong getModuleHandleAddress,
        ulong freeLibraryAddress,
        ulong resultAddress)
    {
        List<byte> code =
        [
            0x48, 0x83, 0xEC, 0x28,
            0x48, 0xB9,
            .. BitConverter.GetBytes(pathAddress),
            0x48, 0xB8,
            .. BitConverter.GetBytes(getModuleHandleAddress),
            0xFF, 0xD0,
            0x48, 0x85, 0xC0
        ];
        int failureJump = AddJump(code, 0x74);
        code.AddRange(
        [
            0x48, 0x89, 0xC1,
            0x48, 0xB8,
            .. BitConverter.GetBytes(freeLibraryAddress),
            0xFF, 0xD0,
            0x48, 0xB9,
            .. BitConverter.GetBytes(resultAddress),
            0x48, 0x89, 0x01,
            0xB8, 0x01, 0x00, 0x00, 0x00,
            0x48, 0x83, 0xC4, 0x28,
            0xC3
        ]);
        int failure = code.Count;
        PatchJump(code, failureJump, failure);
        code.AddRange(
        [
            0x48, 0xB9,
            .. BitConverter.GetBytes(resultAddress),
            0x31, 0xC0,
            0x48, 0x89, 0x01,
            0x48, 0x83, 0xC4, 0x28,
            0xC3
        ]);
        return code.ToArray();
    }

    private static byte[] BuildX86UnloadStub(
        ulong pathAddress,
        ulong getModuleHandleAddress,
        ulong freeLibraryAddress,
        ulong resultAddress)
    {
        uint path = checked((uint)pathAddress);
        uint getModuleHandle = checked((uint)getModuleHandleAddress);
        uint freeLibrary = checked((uint)freeLibraryAddress);
        uint result = checked((uint)resultAddress);
        List<byte> code =
        [
            0x68,
            .. BitConverter.GetBytes(path),
            0xB8,
            .. BitConverter.GetBytes(getModuleHandle),
            0xFF, 0xD0,
            0x85, 0xC0
        ];
        int failureJump = AddJump(code, 0x74);
        code.AddRange(
        [
            0x50,
            0xB8,
            .. BitConverter.GetBytes(freeLibrary),
            0xFF, 0xD0,
            0xA3,
            .. BitConverter.GetBytes(result),
            0xB8, 0x01, 0x00, 0x00, 0x00,
            0xC3
        ]);
        int failure = code.Count;
        PatchJump(code, failureJump, failure);
        code.AddRange(
        [
            0xC7, 0x05,
            .. BitConverter.GetBytes(result),
            0x00, 0x00, 0x00, 0x00,
            0x31, 0xC0,
            0xC3
        ]);
        return code.ToArray();
    }

    private static int AddJump(List<byte> code, byte opcode)
    {
        code.Add(opcode);
        code.Add(0);
        return code.Count - 1;
    }

    private static void PatchJump(
        List<byte> code,
        int displacementOffset,
        int targetOffset)
    {
        int displacement = targetOffset - displacementOffset - 1;
        if (displacement is < sbyte.MinValue or > sbyte.MaxValue)
        {
            throw new InvalidOperationException("Remote stub jump exceeds the short-jump range.");
        }

        code[displacementOffset] = unchecked((byte)(sbyte)displacement);
    }

    private void FreeRemoteAllocation(ulong address)
    {
        if (address != 0)
        {
            FreeMemory(address);
        }
    }

    private static string BuildWin32Error(string operation)
    {
        int error = Marshal.GetLastWin32Error();
        if (error == 0)
        {
            return $"{operation}。";
        }

        return $"{operation}：{new Win32Exception(error).Message} (Win32: {error}, 0x{error:X8})。";
    }

    private void NotifyStateChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
