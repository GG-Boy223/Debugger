using System.Runtime.InteropServices;

namespace DogeDebugger.Core.Native;

internal static class HandleNativeMethods
{
    internal const int SystemExtendedHandleInformation = 64;
    internal const int ObjectTypeInformation = 2;
    internal const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    internal const uint DuplicateSameAccess = 0x00000002;

    [DllImport("ntdll.dll")]
    internal static extern int NtQuerySystemInformation(
        int systemInformationClass,
        IntPtr systemInformation,
        int systemInformationLength,
        out int returnLength);

    [DllImport("ntdll.dll")]
    internal static extern int NtQueryObject(
        IntPtr handle,
        int objectInformationClass,
        IntPtr objectInformation,
        int objectInformationLength,
        out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DuplicateHandle(
        IntPtr sourceProcessHandle,
        IntPtr sourceHandle,
        IntPtr targetProcessHandle,
        out IntPtr targetHandle,
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint options);

    [DllImport("kernel32.dll")]
    internal static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    internal struct UnicodeString
    {
        internal ushort Length;
        internal ushort MaximumLength;
        internal IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SystemHandleInformationEx
    {
        internal IntPtr NumberOfHandles;
        internal IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SystemHandleTableEntryInfoEx
    {
        internal IntPtr Object;
        internal IntPtr UniqueProcessId;
        internal IntPtr HandleValue;
        internal uint GrantedAccess;
        internal ushort CreatorBackTraceIndex;
        internal ushort ObjectTypeIndex;
        internal uint HandleAttributes;
        internal uint Reserved;
    }
}
