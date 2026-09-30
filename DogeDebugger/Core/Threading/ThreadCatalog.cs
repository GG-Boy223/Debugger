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
                    NtQueryInformationThread(
                        threadHandle.DangerousGetHandle(),
                        ThreadBasicInformation,
                        out ThreadBasicInformationData basicInformation,
                        Marshal.SizeOf<ThreadBasicInformationData>(),
                        out _) >= 0)
                {
                    tebBaseAddress = unchecked((ulong)basicInformation.TebBaseAddress.ToInt64());
                }

                threads.Add(new ThreadDescriptor
                {
                    ThreadId = nativeEntry.ThreadId,
                    ProcessId = nativeEntry.OwnerProcessId,
                    BasePriority = nativeEntry.BasePriority,
                    Name = name ?? $"Thread {nativeEntry.ThreadId}",
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

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationThread(
        IntPtr threadHandle,
        int informationClass,
        out ThreadBasicInformationData threadInformation,
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
