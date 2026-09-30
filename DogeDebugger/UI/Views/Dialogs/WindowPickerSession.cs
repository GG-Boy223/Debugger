using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace DogeDebugger.UI.Views.Dialogs;

internal sealed class WindowPickerSession : IDisposable
{
    private const uint GaRoot = 2;
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExNoActivate = 0x08000000L;

    private readonly Window _owner;
    private readonly FrameworkElement _captureElement;
    private readonly Window _overlay;
    private readonly Border _overlayBorder;
    private readonly TextBlock _overlayText;
    private bool _isPicking;
    private bool _isDisposed;

    public WindowPickerSession(
        Window owner,
        FrameworkElement captureElement)
    {
        _owner = owner;
        _captureElement = captureElement;
        _overlayText = new TextBlock
        {
            FontSize = 12,
            Foreground = Brushes.White,
            Margin = new Thickness(6, 2, 6, 2),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Top
        };
        _overlayBorder = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 10, 132, 255)),
            BorderThickness = new Thickness(2),
            Background = new SolidColorBrush(Color.FromArgb(0xB0, 24, 24, 24)),
            Child = _overlayText
        };
        _overlay = new Window
        {
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Content = _overlayBorder,
            IsHitTestVisible = false,
            ResizeMode = ResizeMode.NoResize,
            ShowActivated = false,
            ShowInTaskbar = false,
            SizeToContent = SizeToContent.WidthAndHeight,
            Topmost = true,
            WindowStyle = WindowStyle.None
        };
        _overlay.SourceInitialized += OnOverlaySourceInitialized;
    }

    public event Action<WindowPickResult>? Picked;

    public void Start()
    {
        if (_isPicking || _isDisposed)
        {
            return;
        }

        _isPicking = true;
        _captureElement.PreviewMouseMove += OnPreviewMouseMove;
        _captureElement.PreviewMouseLeftButtonUp += OnPreviewMouseLeftButtonUp;
        _captureElement.LostMouseCapture += OnLostMouseCapture;
        _owner.PreviewKeyDown += OnOwnerPreviewKeyDown;
        Mouse.Capture(_captureElement, CaptureMode.Element);
        UpdateOverlay();
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        StopCore();
        _overlay.SourceInitialized -= OnOverlaySourceInitialized;
        _overlay.Close();
    }

    private void OnPreviewMouseMove(
        object sender,
        MouseEventArgs eventArgs)
    {
        if (_isPicking)
        {
            UpdateOverlay();
            eventArgs.Handled = true;
        }
    }

    private void OnPreviewMouseLeftButtonUp(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        if (!_isPicking)
        {
            return;
        }

        WindowPickResult? result = ReadWindowAtCursor();
        StopCore();
        eventArgs.Handled = true;
        if (result is not null)
        {
            Picked?.Invoke(result);
        }
    }

    private void OnLostMouseCapture(
        object sender,
        MouseEventArgs eventArgs)
    {
        if (_isPicking && Mouse.Captured is null)
        {
            StopCore();
        }
    }

    private void OnOwnerPreviewKeyDown(
        object sender,
        KeyEventArgs eventArgs)
    {
        if (_isPicking && eventArgs.Key == Key.Escape)
        {
            StopCore();
            eventArgs.Handled = true;
        }
    }

    private void StopCore()
    {
        if (!_isPicking)
        {
            return;
        }

        _isPicking = false;
        _captureElement.PreviewMouseMove -= OnPreviewMouseMove;
        _captureElement.PreviewMouseLeftButtonUp -= OnPreviewMouseLeftButtonUp;
        _captureElement.LostMouseCapture -= OnLostMouseCapture;
        _owner.PreviewKeyDown -= OnOwnerPreviewKeyDown;
        if (Mouse.Captured == _captureElement)
        {
            Mouse.Capture(null);
        }

        _overlay.Hide();
    }

    private void OnOverlaySourceInitialized(
        object? sender,
        EventArgs eventArgs)
    {
        IntPtr windowHandle = new WindowInteropHelper(_overlay).Handle;
        long extendedStyle = GetWindowLongPtr(windowHandle, GwlExStyle).ToInt64();
        SetWindowLongPtr(
            windowHandle,
            GwlExStyle,
            new IntPtr(
                extendedStyle |
                WsExToolWindow |
                WsExTransparent |
                WsExNoActivate));
    }

    private void UpdateOverlay()
    {
        WindowPickResult? result = ReadWindowAtCursor();
        if (result is null)
        {
            if (_overlay.IsVisible)
            {
                _overlay.Hide();
            }

            return;
        }

        Rect bounds = result.Bounds;
        _overlay.Left = bounds.Left;
        _overlay.Top = bounds.Top;
        _overlay.Width = Math.Max(1, bounds.Width);
        _overlay.Height = Math.Max(1, bounds.Height);
        _overlayText.Text = string.IsNullOrWhiteSpace(result.Title)
            ? $"PID: {result.ProcessId}"
            : $"PID: {result.ProcessId} - {result.Title}";
        if (!_overlay.IsVisible)
        {
            _overlay.Show();
        }
    }

    private WindowPickResult? ReadWindowAtCursor()
    {
        if (!GetCursorPos(out NativePoint cursor))
        {
            return null;
        }

        IntPtr windowHandle = WindowFromPoint(cursor);
        IntPtr rootWindow = GetAncestor(windowHandle, GaRoot);
        if (rootWindow != IntPtr.Zero)
        {
            windowHandle = rootWindow;
        }

        if (windowHandle == IntPtr.Zero ||
            windowHandle == new WindowInteropHelper(_owner).Handle ||
            !GetWindowRect(windowHandle, out NativeRect bounds) ||
            bounds.Right <= bounds.Left ||
            bounds.Bottom <= bounds.Top)
        {
            return null;
        }

        long extendedStyle = GetWindowLongPtr(windowHandle, GwlExStyle).ToInt64();
        if ((extendedStyle & WsExToolWindow) != 0 &&
            GetWindowTextLength(windowHandle) == 0)
        {
            return null;
        }

        _ = GetWindowThreadProcessId(windowHandle, out uint processId);
        if (processId == 0 || processId > int.MaxValue)
        {
            return null;
        }

        DpiScale dpi = VisualTreeHelper.GetDpi(_owner);
        return new WindowPickResult(
            windowHandle,
            (int)processId,
            ReadWindowTitle(windowHandle),
            new Rect(
                bounds.Left / dpi.DpiScaleX,
                bounds.Top / dpi.DpiScaleY,
                (bounds.Right - bounds.Left) / dpi.DpiScaleX,
                (bounds.Bottom - bounds.Top) / dpi.DpiScaleY));
    }

    private static string ReadWindowTitle(IntPtr windowHandle)
    {
        int length = GetWindowTextLength(windowHandle);
        if (length <= 0)
        {
            return string.Empty;
        }

        StringBuilder title = new(length + 1);
        _ = GetWindowText(windowHandle, title, title.Capacity);
        return title.ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr WindowFromPoint(NativePoint point);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetAncestor(IntPtr windowHandle, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(
        IntPtr windowHandle,
        out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(
        IntPtr windowHandle,
        out NativeRect bounds);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowTextLength(IntPtr windowHandle);

    [DllImport(
        "user32.dll",
        CharSet = CharSet.Unicode,
        EntryPoint = "GetWindowTextW",
        SetLastError = true)]
    private static extern int GetWindowText(
        IntPtr windowHandle,
        StringBuilder text,
        int maximumCount);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(
        IntPtr windowHandle,
        int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(
        IntPtr windowHandle,
        int index,
        IntPtr value);
}

internal sealed record WindowPickResult(
    IntPtr WindowHandle,
    int ProcessId,
    string Title,
    Rect Bounds);
