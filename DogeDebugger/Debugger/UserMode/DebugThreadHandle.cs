using Microsoft.Win32.SafeHandles;

namespace DogeDebugger.Debugger.UserMode;

internal sealed class DebugThreadHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal DebugThreadHandle(IntPtr handle)
        : base(ownsHandle: true)
    {
        SetHandle(handle);
    }

    public override bool IsInvalid => base.IsInvalid || DangerousGetHandle() == IntPtr.Zero;

    protected override bool ReleaseHandle() => NativeDebuggerMethods.CloseHandle(handle);
}
