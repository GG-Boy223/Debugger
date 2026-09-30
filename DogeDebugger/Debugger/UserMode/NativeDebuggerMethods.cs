using System.Runtime.InteropServices;

namespace DogeDebugger.Debugger.UserMode;

internal static class NativeDebuggerMethods
{
    internal const uint DebugOnlyThisProcess = 0x00000002;
    internal const uint CreateUnicodeEnvironment = 0x00000400;

    internal const uint DbContinue = 0x00010002;
    internal const uint DbExceptionNotHandled = 0x80010001;

    internal const uint ThreadGetContext = 0x0008;
    internal const uint ThreadSetContext = 0x0010;
    internal const uint ThreadSuspendResume = 0x0002;
    internal const uint ThreadQueryInformation = 0x0040;

    internal const uint ContextAmd64 = 0x00100000;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DebugActiveProcess(uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DebugActiveProcessStop(uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DebugSetProcessKillOnExit(
        [MarshalAs(UnmanagedType.Bool)] bool killOnExit);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WaitForDebugEvent(
        out DebugEvent debugEvent,
        uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ContinueDebugEvent(
        uint processId,
        uint threadId,
        uint continueStatus);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DebugBreakProcess(IntPtr processHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenThread(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern unsafe bool GetThreadContext(
        IntPtr threadHandle,
        Context64* context);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern unsafe bool SetThreadContext(
        IntPtr threadHandle,
        Context64* context);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern unsafe bool Wow64GetThreadContext(
        IntPtr threadHandle,
        Context32* context);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern unsafe bool Wow64SetThreadContext(
        IntPtr threadHandle,
        Context32* context);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool FlushInstructionCache(
        IntPtr processHandle,
        IntPtr baseAddress,
        UIntPtr size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateProcessW(
        string? applicationName,
        string commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct StartupInfo
    {
        internal int Size;
        internal string? Reserved;
        internal string? Desktop;
        internal string? Title;
        internal int X;
        internal int Y;
        internal int XSize;
        internal int YSize;
        internal int XCountChars;
        internal int YCountChars;
        internal int FillAttribute;
        internal int Flags;
        internal short ShowWindow;
        internal short Reserved2;
        internal IntPtr Reserved2Pointer;
        internal IntPtr StandardInput;
        internal IntPtr StandardOutput;
        internal IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessInformation
    {
        internal IntPtr Process;
        internal IntPtr Thread;
        internal uint ProcessId;
        internal uint ThreadId;
    }

    internal enum DebugEventCode : uint
    {
        Exception = 1,
        CreateThread = 2,
        CreateProcess = 3,
        ExitThread = 4,
        ExitProcess = 5,
        LoadDll = 6,
        UnloadDll = 7,
        OutputDebugString = 8,
        Rip = 9
    }

    [StructLayout(LayoutKind.Explicit, Size = 176)]
    internal unsafe struct DebugEvent
    {
        [FieldOffset(0)]
        internal DebugEventCode Code;

        [FieldOffset(4)]
        internal uint ProcessId;

        [FieldOffset(8)]
        internal uint ThreadId;

        [FieldOffset(16)]
        internal DebugEventUnion Union;
    }

    [StructLayout(LayoutKind.Explicit, Size = 160)]
    internal struct DebugEventUnion
    {
        [FieldOffset(0)]
        internal ExceptionDebugInfo Exception;

        [FieldOffset(0)]
        internal CreateThreadDebugInfo CreateThread;

        [FieldOffset(0)]
        internal CreateProcessDebugInfo CreateProcess;

        [FieldOffset(0)]
        internal ExitThreadDebugInfo ExitThread;

        [FieldOffset(0)]
        internal ExitProcessDebugInfo ExitProcess;

        [FieldOffset(0)]
        internal LoadDllDebugInfo LoadDll;

        [FieldOffset(0)]
        internal UnloadDllDebugInfo UnloadDll;

        [FieldOffset(0)]
        internal OutputDebugStringInfo OutputDebugString;

        [FieldOffset(0)]
        internal RipInfo Rip;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct ExceptionDebugInfo
    {
        internal ExceptionRecord ExceptionRecord;
        internal uint FirstChance;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct ExceptionRecord
    {
        internal uint ExceptionCode;
        internal uint ExceptionFlags;
        internal IntPtr ExceptionRecordPointer;
        internal ulong ExceptionAddress;
        internal uint NumberParameters;
        internal uint Alignment;
        internal fixed ulong ExceptionInformation[15];
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CreateThreadDebugInfo
    {
        internal IntPtr Thread;
        internal IntPtr ThreadLocalBase;
        internal IntPtr StartAddress;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CreateProcessDebugInfo
    {
        internal IntPtr File;
        internal IntPtr Process;
        internal IntPtr Thread;
        internal IntPtr ImageBase;
        internal uint DebugInfoFileOffset;
        internal uint DebugInfoSize;
        internal IntPtr ThreadLocalBase;
        internal IntPtr StartAddress;
        internal IntPtr ImageName;
        internal ushort Unicode;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ExitThreadDebugInfo
    {
        internal uint ExitCode;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ExitProcessDebugInfo
    {
        internal uint ExitCode;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LoadDllDebugInfo
    {
        internal IntPtr File;
        internal IntPtr BaseOfDll;
        internal uint DebugInfoFileOffset;
        internal uint DebugInfoSize;
        internal IntPtr ImageName;
        internal ushort Unicode;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct UnloadDllDebugInfo
    {
        internal IntPtr BaseOfDll;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct OutputDebugStringInfo
    {
        internal IntPtr DebugStringData;
        internal ushort Unicode;
        internal ushort DebugStringLength;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RipInfo
    {
        internal uint Error;
        internal uint Type;
    }

    [StructLayout(LayoutKind.Explicit, Size = 1232)]
    internal struct Context64
    {
        [FieldOffset(0x30)]
        internal uint ContextFlags;

        [FieldOffset(0x38)]
        internal ushort SegmentCs;

        [FieldOffset(0x3A)]
        internal ushort SegmentDs;

        [FieldOffset(0x3C)]
        internal ushort SegmentEs;

        [FieldOffset(0x3E)]
        internal ushort SegmentFs;

        [FieldOffset(0x40)]
        internal ushort SegmentGs;

        [FieldOffset(0x42)]
        internal ushort SegmentSs;

        [FieldOffset(0x44)]
        internal uint EFlags;

        [FieldOffset(0x48)]
        internal ulong Dr0;

        [FieldOffset(0x50)]
        internal ulong Dr1;

        [FieldOffset(0x58)]
        internal ulong Dr2;

        [FieldOffset(0x60)]
        internal ulong Dr3;

        [FieldOffset(0x68)]
        internal ulong Dr6;

        [FieldOffset(0x70)]
        internal ulong Dr7;

        [FieldOffset(0x78)]
        internal ulong Rax;

        [FieldOffset(0x80)]
        internal ulong Rcx;

        [FieldOffset(0x88)]
        internal ulong Rdx;

        [FieldOffset(0x90)]
        internal ulong Rbx;

        [FieldOffset(0x98)]
        internal ulong Rsp;

        [FieldOffset(0xA0)]
        internal ulong Rbp;

        [FieldOffset(0xA8)]
        internal ulong Rsi;

        [FieldOffset(0xB0)]
        internal ulong Rdi;

        [FieldOffset(0xB8)]
        internal ulong R8;

        [FieldOffset(0xC0)]
        internal ulong R9;

        [FieldOffset(0xC8)]
        internal ulong R10;

        [FieldOffset(0xD0)]
        internal ulong R11;

        [FieldOffset(0xD8)]
        internal ulong R12;

        [FieldOffset(0xE0)]
        internal ulong R13;

        [FieldOffset(0xE8)]
        internal ulong R14;

        [FieldOffset(0xF0)]
        internal ulong R15;

        [FieldOffset(0xF8)]
        internal ulong Rip;
    }

    [StructLayout(LayoutKind.Explicit, Size = 716)]
    internal struct Context32
    {
        [FieldOffset(0)]
        internal uint ContextFlags;

        [FieldOffset(4)]
        internal uint Dr0;

        [FieldOffset(8)]
        internal uint Dr1;

        [FieldOffset(12)]
        internal uint Dr2;

        [FieldOffset(16)]
        internal uint Dr3;

        [FieldOffset(20)]
        internal uint Dr6;

        [FieldOffset(24)]
        internal uint Dr7;

        [FieldOffset(156)]
        internal uint Edi;

        [FieldOffset(160)]
        internal uint Esi;

        [FieldOffset(164)]
        internal uint Ebx;

        [FieldOffset(168)]
        internal uint Edx;

        [FieldOffset(172)]
        internal uint Ecx;

        [FieldOffset(176)]
        internal uint Eax;

        [FieldOffset(180)]
        internal uint Ebp;

        [FieldOffset(184)]
        internal uint Eip;

        [FieldOffset(188)]
        internal uint SegmentCs;

        [FieldOffset(192)]
        internal uint EFlags;

        [FieldOffset(196)]
        internal uint Esp;

        [FieldOffset(200)]
        internal uint SegmentSs;
    }
}
