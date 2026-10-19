using System.Runtime.InteropServices;
using Windows.Graphics;

namespace LiveCaptions.Desktop;

public sealed partial class MainWindow
{
    private async Task VerifyMonitorFrameAsync(Action<bool, string> check)
    {
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var rect = new FrameRect { Left = 100, Top = 100, Right = 900, Bottom = 300 };
        var memory = Marshal.AllocHGlobal(Marshal.SizeOf<FrameRect>());
        try
        {
            Marshal.StructureToPtr(rect, memory, false);
            FrameSendMessage(handle, 0x83, IntPtr.Zero, memory); // WM_NCCALCSIZE / RECT form
            var result = Marshal.PtrToStructure<FrameRect>(memory);
            check(result.Left == rect.Left && result.Top == rect.Top && result.Right == rect.Right && result.Bottom == rect.Bottom,
                $"Simple non-client calculation preserves the full borderless client rectangle ({result.Left},{result.Top},{result.Right},{result.Bottom})");
        }
        finally { Marshal.FreeHGlobal(memory); }

        var original = _appearance;
        var bounds = new RectInt32(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
        var displays = Microsoft.UI.Windowing.DisplayArea.FindAll();
        try
        {
            UpdateAppearance(original with { BackgroundColor = "#000000", BackgroundOpacity = 1 });
            foreach (var round in Enumerable.Range(0, 2))
            for (var displayIndex = 0; displayIndex < displays.Count; displayIndex++)
            {
                var display = displays[displayIndex];
                var area = display.WorkArea;
                var origin = AppWindow.Position;
                var target = new PointInt32(area.X + Math.Max(0, (area.Width - AppWindow.Size.Width) / 2), area.Y + 100);
                for (var step = 1; step <= 12; step++)
                {
                    AppWindow.Move(new(origin.X + (target.X - origin.X) * step / 12, origin.Y + (target.Y - origin.Y) * step / 12));
                    await Task.Delay(10);
                }
                Activate(); NativeWindowStyle.RaiseTopmost(this);
                await Task.Delay(220);
                _root.UpdateLayout();
                var scale = _windowStyle.Scale;
                check(FrameDwmGetWindowAttribute(handle, 1, out var rendering, 4) == 0 && rendering == 0,
                    $"DWM non-client frame rendering is disabled on monitor {display.DisplayId.Value}");
                check(_windowStyle.IsFullyClient && _windowStyle.HasResizeFrame,
                    $"Monitor transition {round + 1}/{display.DisplayId.Value} at {scale * 100:F0}% keeps a full client surface and resize frame");
                var points = new[]
                {
                    new Windows.Foundation.Point(3, _root.ActualHeight / 2),
                    new Windows.Foundation.Point(_root.ActualWidth - 4, _root.ActualHeight / 2),
                    new Windows.Foundation.Point(_root.ActualWidth / 2, 3),
                    new Windows.Foundation.Point(_root.ActualWidth / 2, _root.ActualHeight - 4)
                };
                NativeWindowStyle.RaiseTopmost(this);
                await Task.Delay(80);
                var colors = points.Select(ReadComposedPixel).ToArray();
                check(colors.All(c => c.R < 25 && c.G < 25 && c.B < 25),
                    "Actual desktop pixels inside all four edges contain the caption background without a thick system frame: " + string.Join(",", colors.Select(c => $"{c.R}/{c.G}/{c.B}")) +
                    $"; root {_root.ActualWidth}/{_root.ActualHeight}; surface {_surface.ActualWidth}/{_surface.ActualHeight}; " + _windowStyle.DescribePoint(points[0]));
                // Full client bounds alone do not prevent DefWindowProc from
                // painting a sizing frame over that client area on activation.
                foreach (var active in new[] { false, true })
                {
                    FrameSendMessage(handle, 0x86, active ? new IntPtr(1) : IntPtr.Zero, IntPtr.Zero); // WM_NCACTIVATE
                    FrameSendMessage(handle, 0x85, new IntPtr(1), IntPtr.Zero); // WM_NCPAINT / whole frame
                    await Task.Delay(80);
                    colors = points.Select(ReadComposedPixel).ToArray();
                    check(colors.All(c => c.R < 25 && c.G < 25 && c.B < 25),
                        $"Non-client repaint (active={active}) cannot overlay a thick frame on monitor {display.DisplayId.Value}: " +
                        string.Join(",", colors.Select(c => $"{c.R}/{c.G}/{c.B}")));
                }
            }
            // Emulate the system rebuilding its frame state on a theme change.
            // The application must restore the no-frame policy after default handling.
            var enabled = 2; // DWMNCRP_ENABLED
            Marshal.ThrowExceptionForHR(FrameDwmSetWindowAttribute(handle, 2, ref enabled, 4));
            FrameSendMessage(handle, 0x31A, IntPtr.Zero, IntPtr.Zero); // WM_THEMECHANGED
            check(FrameDwmGetWindowAttribute(handle, 1, out var restored, 4) == 0 && restored == 0,
                "Theme changes restore disabled system frame rendering after default handling");
            check(true, $"Monitor frame regression visited {displays.Count} displays twice");
        }
        finally { UpdateAppearance(original); AppWindow.MoveAndResize(bounds); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct FrameRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern IntPtr FrameSendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] private static extern int FrameDwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute")] private static extern int FrameDwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
