using System.Runtime.InteropServices;

namespace DogeDebugger.Core.Native;

internal static class NativeMethods
{
    internal const uint ProcessTerminate = 0x0001;
    internal const uint ProcessCreateThread = 0x0002;
    internal const uint ProcessSetInformation = 0x0200;
    internal const uint ProcessQueryInformation = 0x0400;
    internal const uint ProcessSuspendResume = 0x0800;
    internal const uint ProcessVirtualMemoryOperation = 0x0008;
    internal const uint ProcessVirtualMemoryRead = 0x0010;
    internal const uint ProcessVirtualMemoryWrite = 0x0020;

    internal const uint Th32csSnapshotModule = 0x00000008;
    internal const uint Th32csSnapshotThread = 0x00000004;

    internal const uint MemCommit = 0x00001000;
    internal const uint MemReserve = 0x00002000;
    internal const uint MemRelease = 0x00008000;
    internal const uint Infinite = 0xFFFFFFFF;
    internal const uint WaitObject0 = 0x00000000;
    internal const uint WaitTimeout = 0x00000102;
    internal const uint MemPrivate = 0x00020000;
    internal const uint MemMapped = 0x00040000;
    internal const uint MemImage = 0x01000000;

    internal const uint PageNoAccess = 0x01;
    internal const uint PageReadOnly = 0x02;
    internal const uint PageReadWrite = 0x04;
    internal const uint PageWriteCopy = 0x08;
    internal const uint PageExecute = 0x10;
    internal const uint PageExecuteRead = 0x20;
    internal const uint PageExecuteReadWrite = 0x40;
    internal const uint PageExecuteWriteCopy = 0x80;
    internal const uint PageGuard = 0x100;
    internal const uint PageNoCache = 0x200;
    internal const uint PageWriteCombine = 0x400;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern unsafe bool ReadProcessMemory(
        IntPtr processHandle,
        void* baseAddress,
        void* buffer,
        UIntPtr size,
        out UIntPtr bytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern unsafe bool WriteProcessMemory(
        IntPtr processHandle,
        void* baseAddress,
        void* buffer,
        UIntPtr size,
        out UIntPtr bytesWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern UIntPtr VirtualQueryEx(
        IntPtr processHandle,
        UIntPtr address,
        out MemoryBasicInformation64 buffer,
        UIntPtr length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool VirtualProtectEx(
        IntPtr processHandle,
        UIntPtr address,
        UIntPtr size,
        uint newProtection,
        out uint oldProtection);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr VirtualAllocEx(
        IntPtr processHandle,
        IntPtr preferredAddress,
        UIntPtr size,
        uint allocationType,
        uint protection);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool VirtualFreeEx(
        IntPtr processHandle,
        IntPtr address,
        UIntPtr size,
        uint freeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr CreateRemoteThread(
        IntPtr processHandle,
        IntPtr threadAttributes,
        UIntPtr stackSize,
        IntPtr startAddress,
        IntPtr parameter,
        uint creationFlags,
        out uint threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint WaitForSingleObject(
        IntPtr handle,
        uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetExitCodeThread(
        IntPtr threadHandle,
        out uint exitCode);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr GetModuleHandleW(string moduleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    internal static extern IntPtr GetProcAddress(
        IntPtr moduleHandle,
        string procedureName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWow64Process(
        IntPtr processHandle,
        [MarshalAs(UnmanagedType.Bool)] out bool isWow64);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Module32FirstW(IntPtr snapshot, ref ModuleEntry32W entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Module32NextW(IntPtr snapshot, ref ModuleEntry32W entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Thread32First(IntPtr snapshot, ref ThreadEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Thread32Next(IntPtr snapshot, ref ThreadEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryFullProcessImageNameW(
        IntPtr processHandle,
        uint flags,
        [Out] char[] fileName,
        ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenThread(uint desiredAccess, bool inheritHandle, uint threadId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetThreadDescription(
        IntPtr threadHandle,
        out IntPtr description);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint SuspendThread(IntPtr threadHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern int ResumeThread(IntPtr threadHandle);

    [DllImport("ntdll.dll")]
    internal static extern int NtSuspendProcess(IntPtr processHandle);

    [DllImport("ntdll.dll")]
    internal static extern int NtResumeProcess(IntPtr processHandle);

    [StructLayout(LayoutKind.Sequential)]
    internal struct MemoryBasicInformation64
    {
        internal ulong BaseAddress;
        internal ulong AllocationBase;
        internal uint AllocationProtect;
        internal uint Alignment1;
        internal ulong RegionSize;
        internal uint State;
        internal uint Protect;
        internal uint Type;
        internal uint Alignment2;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct ModuleEntry32W
    {
        internal uint Size;
        internal uint ModuleId;
        internal uint ProcessId;
        internal uint GlobalUsageCount;
        internal uint ProcessUsageCount;
        internal IntPtr BaseAddress;
        internal uint BaseSize;
        internal IntPtr ModuleHandle;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        internal string ModuleName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        internal string ExecutablePath;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ThreadEntry32
    {
        internal uint Size;
        internal uint UsageCount;
        internal uint ThreadId;
        internal uint OwnerProcessId;
        internal int BasePriority;
        internal int DeltaPriority;
        internal uint Flags;
    }
}
