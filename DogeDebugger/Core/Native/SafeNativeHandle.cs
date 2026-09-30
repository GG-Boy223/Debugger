using Microsoft.Win32.SafeHandles;

namespace DogeDebugger.Core.Native;

internal sealed class SafeNativeHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SafeNativeHandle(IntPtr handle)
        : base(ownsHandle: true)
    {
        SetHandle(handle);
    }

    protected override bool ReleaseHandle()
    {
        return NativeMethods.CloseHandle(handle);
    }
}
