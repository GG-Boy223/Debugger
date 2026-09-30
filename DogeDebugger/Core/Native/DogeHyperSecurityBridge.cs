using System.Runtime.InteropServices;
using System.Text;

namespace DogeDebugger.Core.Native;

internal readonly record struct DogeHyperSecurityResult(int Status, ulong ReturnValue);

internal static class DogeHyperSecurityBridge
{
    private const int DhsCommandProcessHandle = 3;
    private const int DhsInjectCommand = 23;

    private static readonly string[] DriverNameMarkers =
    [
        "DogeHyperSecurity",
        "DogeHyper",
        "DHS"
    ];

    public static bool IsLoaded()
    {
        try
        {
            IntPtr[] addresses = new IntPtr[1024];
            uint requiredBytes = 0;
            if (!EnumDeviceDrivers(
                    addresses,
                    checked((uint)(addresses.Length * IntPtr.Size)),
                    out requiredBytes))
            {
                return false;
            }

            int count = Math.Min(
                addresses.Length,
                checked((int)(requiredBytes / (uint)IntPtr.Size)));
            StringBuilder name = new(512);
            for (int index = 0; index < count; index++)
            {
                name.Clear();
                uint length = GetDeviceDriverBaseName(
                    addresses[index],
                    name,
                    checked((uint)name.Capacity));
                if (length == 0)
                {
                    continue;
                }

                string driverName = name.ToString();
                foreach (string marker in DriverNameMarkers)
                {
                    if (driverName.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }
        catch
        {
        }

        return false;
    }

    public static DogeHyperSecurityResult InjectBytes(
        int processId,
        byte[] payload,
        ulong targetAddress)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (processId <= 0 || payload.Length == 0)
        {
            return new DogeHyperSecurityResult(-1, 0);
        }

        IntPtr payloadAddress = IntPtr.Zero;
        IntPtr requestAddress = IntPtr.Zero;
        try
        {
            payloadAddress = Marshal.AllocHGlobal(payload.Length);
            Marshal.Copy(payload, 0, payloadAddress, payload.Length);

            InjectionRequest request = new()
            {
                ProcessId = unchecked((ulong)processId),
                PayloadAddress = unchecked((ulong)payloadAddress.ToInt64()),
                PayloadLength = unchecked((ulong)payload.Length),
                TargetAddress = targetAddress
            };

            requestAddress = Marshal.AllocHGlobal(Marshal.SizeOf<InjectionRequest>());
            Marshal.StructureToPtr(request, requestAddress, fDeleteOld: false);
            int status = NtDuplicateObject(
                new IntPtr(DhsCommandProcessHandle),
                new IntPtr(DhsInjectCommand),
                IntPtr.Zero,
                requestAddress,
                0,
                0,
                0);
            InjectionRequest result = Marshal.PtrToStructure<InjectionRequest>(requestAddress);
            return new DogeHyperSecurityResult(status, result.ReturnValue);
        }
        finally
        {
            if (requestAddress != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(requestAddress);
            }

            if (payloadAddress != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(payloadAddress);
            }
        }
    }

    [DllImport("ntdll.dll")]
    private static extern int NtDuplicateObject(
        IntPtr sourceProcessHandle,
        IntPtr sourceHandle,
        IntPtr targetProcessHandle,
        IntPtr targetHandle,
        uint desiredAccess,
        uint handleAttributes,
        uint options);

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDeviceDrivers(
        [Out] IntPtr[] imageBase,
        uint size,
        out uint needed);

    [DllImport(
        "psapi.dll",
        CharSet = CharSet.Unicode,
        EntryPoint = "GetDeviceDriverBaseNameW",
        SetLastError = true)]
    private static extern uint GetDeviceDriverBaseName(
        IntPtr imageBase,
        [Out] StringBuilder baseName,
        uint size);

    [StructLayout(LayoutKind.Sequential)]
    private struct InjectionRequest
    {
        public ulong ProcessId;
        public ulong PayloadAddress;
        public ulong PayloadLength;
        public ulong TargetAddress;
        public ulong ReturnValue;
        public ulong Reserved;
    }
}
