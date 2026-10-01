using System.Runtime.InteropServices;
using DogeDebugger.Core.Native;
using DogeDebugger.Core.Process;

namespace DogeDebugger.Core.Threading;

public sealed class ThreadCatalog
{
    private const uint ThreadQueryInformation = 0x0040;
    private const uint ThreadSuspendResume = 0x0002;

    public IReadOnlyList<ThreadDescriptor> Enumerate(ITargetProcess process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!process.IsOpen)
        {
            return [];
        }

        using SafeNativeHandle snapshot = new(
            NativeMethods.CreateToolhelp32Snapshot(
                NativeMethods.Th32csSnapshotThread,
                checked((uint)process.ProcessId)));

        if (snapshot.IsInvalid)
        {
            return [];
        }

        NativeMethods.ThreadEntry32 nativeEntry = new()
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.ThreadEntry32>()
        };

        List<ThreadDescriptor> threads = [];
        bool hasEntry = NativeMethods.Thread32First(snapshot.DangerousGetHandle(), ref nativeEntry);
        while (hasEntry)
        {
            if (nativeEntry.OwnerProcessId == process.ProcessId)
            {
                using SafeNativeHandle threadHandle = new(
                    NativeMethods.OpenThread(
                        ThreadQueryInformation | ThreadSuspendResume,
                        inheritHandle: false,
                        nativeEntry.ThreadId));

                string? name = null;
                ulong tebBaseAddress = 0;
                ulong startAddress = 0;
                int priority = nativeEntry.BasePriority;
                if (!threadHandle.IsInvalid &&
                    NativeMethods.GetThreadDescription(threadHandle.DangerousGetHandle(), out IntPtr description))
                {
                    try
                    {
                        name = Marshal.PtrToStringUni(description);
                    }
                    finally
                    {
                        if (description != IntPtr.Zero)
                        {
                            NativeMethods.CloseHandle(description);
                        }
                    }
                }

                if (!threadHandle.IsInvalid &&
                    NtQueryInformationThreadStartAddress(
                        threadHandle.DangerousGetHandle(),
                        ThreadQuerySetWin32StartAddress,
                        out IntPtr win32StartAddress,
                        IntPtr.Size,
                        out _) >= 0)
                {
                    startAddress = unchecked((ulong)win32StartAddress.ToInt64());
                }

                if (!threadHandle.IsInvalid &&
                    NtQueryInformationThread(
                        threadHandle.DangerousGetHandle(),
                        ThreadBasicInformation,
                        out ThreadBasicInformationData basicInformation,
                        Marshal.SizeOf<ThreadBasicInformationData>(),
                        out _) >= 0)
                {
                    tebBaseAddress = unchecked((ulong)basicInformation.TebBaseAddress.ToInt64());
                    if (basicInformation.Priority != 0)
                    {
                        priority = basicInformation.Priority;
                    }
                }

                threads.Add(new ThreadDescriptor
                {
                    ThreadId = nativeEntry.ThreadId,
                    ProcessId = nativeEntry.OwnerProcessId,
                    BasePriority = priority,
                    Name = name ?? string.Empty,
                    StartAddress = startAddress,
                    TebBaseAddress = tebBaseAddress
                });
            }

            nativeEntry.Size = (uint)Marshal.SizeOf<NativeMethods.ThreadEntry32>();
            hasEntry = NativeMethods.Thread32Next(snapshot.DangerousGetHandle(), ref nativeEntry);
        }

        return threads
            .OrderBy(static thread => thread.ThreadId)
            .ToArray();
    }

    public bool SetSuspended(uint threadId, bool suspended)
    {
        using SafeNativeHandle threadHandle = new(
            NativeMethods.OpenThread(
                ThreadSuspendResume,
                inheritHandle: false,
                threadId));
        if (threadHandle.IsInvalid)
        {
            return false;
        }

        return suspended
            ? NativeMethods.SuspendThread(threadHandle.DangerousGetHandle()) != uint.MaxValue
            : NativeMethods.ResumeThread(threadHandle.DangerousGetHandle()) != -1;
    }

    private const int ThreadBasicInformation = 0;
    private const int ThreadQuerySetWin32StartAddress = 9;

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationThread(
        IntPtr threadHandle,
        int informationClass,
        out ThreadBasicInformationData threadInformation,
        int threadInformationLength,
        out int returnLength);

    [DllImport("ntdll.dll", EntryPoint = "NtQueryInformationThread")]
    private static extern int NtQueryInformationThreadStartAddress(
        IntPtr threadHandle,
        int informationClass,
        out IntPtr threadInformation,
        int threadInformationLength,
        out int returnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct ThreadBasicInformationData
    {
        public int ExitStatus;
        public IntPtr TebBaseAddress;
        public IntPtr UniqueProcess;
        public IntPtr UniqueThread;
        public IntPtr AffinityMask;
        public int Priority;
        public int BasePriority;
    }
}
