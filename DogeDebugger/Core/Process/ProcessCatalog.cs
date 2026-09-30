using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DogeDebugger.Core.Process;

public sealed class ProcessCatalog
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    public IReadOnlyList<ProcessDescriptor> Enumerate()
    {
        List<ProcessDescriptor> processes = [];
        foreach (System.Diagnostics.Process process in System.Diagnostics.Process.GetProcesses())
        {
            try
            {
                string? path = null;
                DateTimeOffset? startTime = null;
                int threadCount = 0;
                string? windowTitle = null;

                try
                {
                    path = ReadExecutablePath(process.Id);
                }
                catch
                {
                    // Protected processes can deny path queries without denying
                    // basic process metadata.
                }

                try
                {
                    startTime = process.StartTime;
                }
                catch
                {
                }

                try
                {
                    threadCount = process.Threads.Count;
                }
                catch
                {
                }

                try
                {
                    windowTitle = process.MainWindowTitle;
                }
                catch
                {
                }

                processes.Add(new ProcessDescriptor(
                    process.Id,
                    process.ProcessName,
                    path,
                    threadCount,
                    startTime,
                    windowTitle));
            }
            catch
            {
                // A process can exit between enumeration and metadata reads.
            }
            finally
            {
                process.Dispose();
            }
        }

        return processes
            .OrderBy(static process => process.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static process => process.ProcessId)
            .ToArray();
    }

    private static string? ReadExecutablePath(int processId)
    {
        IntPtr processHandle = OpenProcess(
            ProcessQueryLimitedInformation,
            inheritHandle: false,
            unchecked((uint)processId));
        if (processHandle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            char[] buffer = new char[1024];
            uint length = (uint)buffer.Length;
            return QueryFullProcessImageName(
                processHandle,
                flags: 0,
                buffer,
                ref length)
                ? new string(buffer, 0, checked((int)length))
                : null;
        }
        finally
        {
            CloseHandle(processHandle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        EntryPoint = "QueryFullProcessImageNameW",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        IntPtr processHandle,
        uint flags,
        [Out] char[] executablePath,
        ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
