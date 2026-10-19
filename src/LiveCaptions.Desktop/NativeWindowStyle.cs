using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using Windows.Foundation;
using Microsoft.UI.Input;

namespace LiveCaptions.Desktop;

internal sealed class NativeWindowStyle
{
    private readonly Window _window;
    private readonly IntPtr _handle;
    private readonly SubclassProc _callback;
    private RectInt32 _normalBounds;
    private bool _restoring;
    private readonly bool _caption;
    private readonly InputNonClientPointerSource? _input;
    internal bool IsDragEnabled { get; private set; } = true;
    internal void SetDraggingEnabled(bool enabled)
    {
        if (IsDragEnabled == enabled) return;
        IsDragEnabled = enabled;
        UpdateInputRegions();
    }
    internal double MinimumHeight { get; set; } = 82;
    internal double MinimumWidth { get; set; } = 360;
    internal double Scale => GetDpiForWindow(_handle) / 96d;
    internal static bool TryGetPointerScreenPosition(uint pointerId, PointerDeviceType device, out PointInt32 position)
    {
        // Sampling the current cursor also coalesces queued mouse moves to the
        // latest location. Touch/pen use their own physical screen coordinates.
        NativePoint point;
        if (device == PointerDeviceType.Mouse)
        {
            if (!GetCursorPos(out point)) { position = default; return false; }
        }
        else
        {
            if (!GetPointerInfo(pointerId, out var info)) { position = default; return false; }
            point = info.PixelLocation;
        }
        position = new(point.X, point.Y);
        return true;
    }
    internal bool HasResizeFrame => (GetWindowLongPtr(_handle, -16).ToInt64() & 0x40000) != 0;
    internal int HitTest(Point point)
    {
        var native = new NativePoint((int)(point.X * Scale), (int)(point.Y * Scale));
        ClientToScreen(_handle, ref native);
        return (int)SendMessage(_handle, 0x84, IntPtr.Zero, new IntPtr((native.Y << 16) | (native.X & 0xffff)));
    }
    internal int InputHitTest(Point point)
    {
        var native = new NativePoint((int)(point.X * Scale), (int)(point.Y * Scale));
        ClientToScreen(_handle, ref native);
        var target = WindowFromPoint(native);
        if (target == IntPtr.Zero) return 0;
        return (int)SendMessage(target, 0x84, IntPtr.Zero, new IntPtr((native.Y << 16) | (native.X & 0xffff)));
    }
    internal string DescribePoint(Point point)
    {
        var native = new NativePoint((int)(point.X * Scale), (int)(point.Y * Scale));
        ClientToScreen(_handle, ref native);
        var target = WindowFromPoint(native);
        GetWindowThreadProcessId(target, out var process);
        GetWindowRect(_handle, out var bounds);
        return $"screen {native.X},{native.Y}; window {bounds.Left},{bounds.Top},{bounds.Right},{bounds.Bottom}; target {target}/{process}; self {_handle}/{Environment.ProcessId}; visible {IsWindowVisible(_handle)}; styles {GetWindowLongPtr(_handle, -16).ToInt64():X}/{GetWindowLongPtr(_handle, -20).ToInt64():X}";
    }

    internal NativeWindowStyle(Window window, bool caption = false)
    {
        _window = window;
        _caption = caption;
        _handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        window.AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "App.ico"));
        var dark = 1;
        DwmSetWindowAttribute(_handle, 20, ref dark, 4);
        var rounded = 2;
        DwmSetWindowAttribute(_handle, 33, ref rounded, 4);
        if (window.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = caption;
            if (caption)
            {
                presenter.IsMaximizable = false;
                presenter.SetBorderAndTitleBar(false, false);
            }
        }
        _normalBounds = new(window.AppWindow.Position.X, window.AppWindow.Position.Y,
            window.AppWindow.Size.Width, window.AppWindow.Size.Height);
        _callback = Filter;
        if (!SetWindowSubclass(_handle, _callback, 1, 0)) throw new InvalidOperationException("无法初始化窗口。");
        if (caption)
        {
            DisableSystemFrame(_handle);
            ConfigureTransparentSurface(_handle);
            SetWindowPos(_handle, IntPtr.Zero, 0, 0, 0, 0, 0x37); // FRAMECHANGED, NOMOVE/SIZE/ZORDER/ACTIVATE
            _input = InputNonClientPointerSource.GetForWindowId(window.AppWindow.Id);
            UpdateInputRegions();
        }
        window.Closed += (_, _) => RemoveWindowSubclass(_handle, _callback, 1);
        if (caption) window.AppWindow.Changed += (_, e) =>
        {
            if (e.DidSizeChange) UpdateInputRegions();
            if (_restoring) return;
            if (IsZoomed(_handle))
            {
                _restoring = true;
                ((OverlappedPresenter)window.AppWindow.Presenter).Restore();
                window.AppWindow.MoveAndResize(_normalBounds);
                _restoring = false;
            }
            else if (e.DidPositionChange || e.DidSizeChange)
                _normalBounds = new(window.AppWindow.Position.X, window.AppWindow.Position.Y,
                    window.AppWindow.Size.Width, window.AppWindow.Size.Height);
        };
    }

    private IntPtr Filter(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, nuint id, nuint data)
    {
        // Both RECT (wParam=0) and NCCALCSIZE_PARAMS (wParam=1) must retain
        // the full client area. Letting the simple form reach the default handler
        // reintroduces the DPI-scaled resize border during monitor transitions.
        if (_caption && message == 0x83) return IntPtr.Zero;
        // NCCALCSIZE controls geometry, but the default non-client handlers can
        // still paint a resize frame over the client surface after a monitor or
        // activation change. Keep the frame style for native resizing, not painting.
        if (_caption && message is 0x85 or 0xAE or 0xAF) return IntPtr.Zero; // NCPAINT / UAH caption/frame
        if (_caption && message == 0x86) // WM_NCACTIVATE: preserve activation without repainting.
            return DefSubclassProc(hwnd, message, wParam, new IntPtr(-1));
        if (_caption && message == 0x14 && GetClientRect(hwnd, out var background)) // WM_ERASEBKGND
        {
            FillRect(wParam, ref background, GetStockObject(4)); // BLACK_BRUSH clears redirected alpha.
            return new IntPtr(1);
        }
        if (_caption && message is 0x31E or 0x31A or 0x2E0) // Composition, theme, or DPI changes.
        {
            var result = DefSubclassProc(hwnd, message, wParam, lParam);
            DisableSystemFrame(hwnd);
            if (message == 0x31E) ConfigureTransparentSurface(hwnd);
            return result;
        }
        if (message == 0x24) // WM_GETMINMAXINFO uses physical pixels.
        {
            var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            info.MinTrack = new((int)Math.Ceiling(MinimumWidth * Scale), (int)Math.Ceiling(MinimumHeight * Scale));
            Marshal.StructureToPtr(info, lParam, false);
            return IntPtr.Zero;
        }
        if (_caption)
        {
            if (message == 0x112 && (wParam.ToInt64() & 0xFFF0) == 0xF030 ||
                message == 0xA3 && (wParam.ToInt64() == 2 || wParam.ToInt64() is >= 10 and <= 17))
                return IntPtr.Zero;
            if (message == 0x84 && GetClientRect(hwnd, out var bounds))
            {
                var screenX = (short)(lParam.ToInt64() & 0xffff);
                var screenY = (short)((lParam.ToInt64() >> 16) & 0xffff);
                var point = new NativePoint(screenX, screenY);
                ScreenToClient(hwnd, ref point);
                var x = point.X; var y = point.Y;
                var edge = 7 * Scale;
                var left = x < edge; var right = x >= bounds.Right - bounds.Left - edge;
                var top = y < edge; var bottom = y >= bounds.Bottom - bounds.Top - edge;
                if (top) return new(left ? 13 : right ? 14 : 12);
                if (bottom) return new(left ? 16 : right ? 17 : 15);
                if (left || right) return new(left ? 10 : 11);
                // Blank presses must reach XAML so a short click can clear the
                // selection, and moving while pressed can immediately drag us.
                return new(1);
            }
        }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }
    internal static void ConfigureTransparentSurface(IntPtr handle)
    {
        // A transparent XAML brush alone blends with the opaque HWND backing
        // surface. Enable DWM alpha composition with an empty blur region; this
        // exposes the desktop without blurring it or fading text and buttons.
        var margins = new Margins();
        Marshal.ThrowExceptionForHR(DwmExtendFrameIntoClientArea(handle, ref margins));
        var region = CreateRectRgn(-2, -2, -1, -1);
        try
        {
            var blur = new BlurBehind { Flags = 3, Enable = 1, Region = region };
            Marshal.ThrowExceptionForHR(DwmEnableBlurBehindWindow(handle, ref blur));
        }
        finally { DeleteObject(region); }
    }
    private static void DisableSystemFrame(IntPtr handle)
    {
        var disabled = 1; // DWMWA_NCRENDERING_POLICY / DWMNCRP_DISABLED
        Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(handle, 2, ref disabled, 4));
        var noBorder = -2; // DWMWA_BORDER_COLOR / DWMWA_COLOR_NONE
        DwmSetWindowAttribute(handle, 34, ref noBorder, 4);
    }
    private void UpdateInputRegions()
    {
        if (_input == null || !GetClientRect(_handle, out var rect)) return;
        var width = rect.Right; var height = rect.Bottom;
        var edge = Math.Max(1, (int)Math.Ceiling(6 * Scale));
        _input.SetRegionRects(NonClientRegionKind.TopBorder, [new(0, 0, width, edge)]);
        _input.SetRegionRects(NonClientRegionKind.BottomBorder, [new(0, Math.Max(0, height - edge), width, edge)]);
        _input.SetRegionRects(NonClientRegionKind.LeftBorder, [new(0, 0, edge, height)]);
        _input.SetRegionRects(NonClientRegionKind.RightBorder, [new(Math.Max(0, width - edge), 0, edge, height)]);
        // Never let a native caption region bypass XAML click/drag handling.
        _input.ClearRegionRects(NonClientRegionKind.Caption);
    }
    internal bool IsFullyClient
    {
        get
        {
            GetClientRect(_handle, out var client); GetWindowRect(_handle, out var window);
            return client.Right == window.Right - window.Left && client.Bottom == window.Bottom - window.Top;
        }
    }
    internal void SetSize(double width, double height) =>
        _window.AppWindow.Resize(new SizeInt32((int)Math.Ceiling(width * Scale), (int)Math.Ceiling(height * Scale)));
    internal void PlaceBottom()
    {
        var area = DisplayArea.GetFromWindowId(_window.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var size = _window.AppWindow.Size;
        _window.AppWindow.Move(new(area.X + (area.Width - size.Width) / 2, area.Y + area.Height - size.Height - 24));
    }
    internal void KeepInWorkArea()
    {
        var area = DisplayArea.GetFromWindowId(_window.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var position = _window.AppWindow.Position;
        var size = _window.AppWindow.Size;
        var y = Math.Max(area.Y, Math.Min(position.Y, area.Y + area.Height - size.Height));
        if (y != position.Y) _window.AppWindow.Move(new(position.X, y));
    }
    internal static void SetOwner(Window child, Window owner) =>
        SetWindowLongPtr(WinRT.Interop.WindowNative.GetWindowHandle(child), -8,
            WinRT.Interop.WindowNative.GetWindowHandle(owner));
    internal static void Restore(Window window)
    {
        if (window.AppWindow.Presenter is OverlappedPresenter presenter) presenter.Restore();
        window.Activate();
    }
    internal static void RaiseTopmost(Window window)
    {
        if (!SetWindowPos(WinRT.Interop.WindowNative.GetWindowHandle(window), new IntPtr(-1), 0, 0, 0, 0, 0x53))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint(int x, int y) { public int X = x, Y = y; }
    [StructLayout(LayoutKind.Sequential)] private struct PointerInfo
    {
        public uint Type, Id, FrameId, Flags;
        public IntPtr SourceDevice, Target;
        public NativePoint PixelLocation, HimetricLocation, PixelLocationRaw, HimetricLocationRaw;
        public uint Time, HistoryCount;
        public int InputData;
        public uint KeyStates;
        public ulong PerformanceCount;
        public uint ButtonChangeType;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Margins { public int Left, Right, Top, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct BlurBehind { public uint Flags; public int Enable; public IntPtr Region; public int Transition; }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo { public NativePoint Reserved, MaxSize, MaxPosition, MinTrack, MaxTrack; }
    private delegate IntPtr SubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc proc, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc proc, nuint id);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern bool GetPointerInfo(uint pointerId, out PointerInfo info);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
    [DllImport("dwmapi.dll")] private static extern int DwmEnableBlurBehindWindow(IntPtr hwnd, ref BlurBehind blur);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] private static extern IntPtr GetStockObject(int index);
    [DllImport("user32.dll")] private static extern int FillRect(IntPtr dc, ref NativeRect rect, IntPtr brush);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hwnd, ref NativePoint point);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
