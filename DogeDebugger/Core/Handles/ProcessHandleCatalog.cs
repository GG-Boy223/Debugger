using System.Runtime.InteropServices;
using DogeDebugger.Core.Native;
using DogeDebugger.Core.Process;

namespace DogeDebugger.Core.Handles;

public sealed class ProcessHandleCatalog
{
    private const int InitialBufferSize = 1024 * 1024;
    private const int MaximumBufferSize = 256 * 1024 * 1024;

    public IReadOnlyList<ProcessHandleEntry> Enumerate(ITargetProcess process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!process.IsOpen)
        {
            return [];
        }

        IntPtr buffer = QueryHandleTable(out int bufferLength);
        try
        {
            int pointerSize = IntPtr.Size;
            int headerSize = pointerSize * 2;
            int entrySize = Marshal.SizeOf<HandleNativeMethods.SystemHandleTableEntryInfoEx>();
            if (bufferLength < headerSize)
            {
                return [];
            }

            HandleNativeMethods.SystemHandleInformationEx header =
                Marshal.PtrToStructure<HandleNativeMethods.SystemHandleInformationEx>(buffer);
            long count = header.NumberOfHandles.ToInt64();
            long maximumEntries = Math.Max(0, (bufferLength - headerSize) / entrySize);
            count = Math.Min(count, maximumEntries);
            List<ProcessHandleEntry> handles = [];
            IntPtr currentProcess = HandleNativeMethods.GetCurrentProcess();

            for (long index = 0; index < count; index++)
            {
                IntPtr entryAddress = IntPtr.Add(buffer, headerSize + checked((int)(index * entrySize)));
                HandleNativeMethods.SystemHandleTableEntryInfoEx entry =
                    Marshal.PtrToStructure<HandleNativeMethods.SystemHandleTableEntryInfoEx>(entryAddress);
                uint processId = unchecked((uint)entry.UniqueProcessId.ToInt64());
                if (processId != process.ProcessId)
                {
                    continue;
                }

                string typeName = QueryTypeName(
                    process.Handle,
                    currentProcess,
                    entry.HandleValue);
                handles.Add(new ProcessHandleEntry
                {
                    ProcessId = processId,
                    HandleValue = unchecked((ulong)entry.HandleValue.ToInt64()),
                    ObjectAddress = unchecked((ulong)entry.Object.ToInt64()),
                    GrantedAccess = entry.GrantedAccess,
                    TypeName = typeName
                });
            }

            return handles
                .OrderBy(static handle => handle.TypeName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static handle => handle.HandleValue)
                .ToArray();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static IntPtr QueryHandleTable(out int bufferLength)
    {
        int size = InitialBufferSize;
        while (size <= MaximumBufferSize)
        {
            IntPtr buffer = Marshal.AllocHGlobal(size);
            int status = HandleNativeMethods.NtQuerySystemInformation(
                HandleNativeMethods.SystemExtendedHandleInformation,
                buffer,
                size,
                out int required);
            if (status >= 0)
            {
                bufferLength = size;
                return buffer;
            }

            Marshal.FreeHGlobal(buffer);
            if (status != HandleNativeMethods.StatusInfoLengthMismatch)
            {
                Marshal.ThrowExceptionForHR(status);
            }

            size = Math.Max(size * 2, required);
        }

        throw new InvalidOperationException("The system handle table is too large to query.");
    }

    private static string QueryTypeName(
        IntPtr sourceProcess,
        IntPtr currentProcess,
        IntPtr sourceHandle)
    {
        if (!HandleNativeMethods.DuplicateHandle(
                sourceProcess,
                sourceHandle,
                currentProcess,
                out IntPtr duplicated,
                0,
                inheritHandle: false,
                HandleNativeMethods.DuplicateSameAccess))
        {
            return string.Empty;
        }

        try
        {
            int size = 1024;
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                int status = HandleNativeMethods.NtQueryObject(
                    duplicated,
                    HandleNativeMethods.ObjectTypeInformation,
                    buffer,
                    size,
                    out int required);
                if (status == HandleNativeMethods.StatusInfoLengthMismatch && required > size)
                {
                    Marshal.FreeHGlobal(buffer);
                    size = required;
                    buffer = Marshal.AllocHGlobal(size);
                    status = HandleNativeMethods.NtQueryObject(
                        duplicated,
                        HandleNativeMethods.ObjectTypeInformation,
                        buffer,
                        size,
                        out _);
                }

                if (status < 0)
                {
                    return string.Empty;
                }

                IntPtr unicodeStringPointer = IntPtr.Add(buffer, IntPtr.Size == 8 ? 8 : 4);
                HandleNativeMethods.UnicodeString name =
                    Marshal.PtrToStructure<HandleNativeMethods.UnicodeString>(unicodeStringPointer);
                return name.Buffer == IntPtr.Zero || name.Length == 0
                    ? string.Empty
                    : Marshal.PtrToStringUni(name.Buffer, name.Length / 2) ?? string.Empty;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            HandleNativeMethods.CloseHandle(duplicated);
        }
    }
}
