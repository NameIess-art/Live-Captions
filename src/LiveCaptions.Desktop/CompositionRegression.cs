using System.Diagnostics;
using System.Runtime.InteropServices;
using LiveCaptions.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace LiveCaptions.Desktop;

public sealed partial class MainWindow
{
    private async Task VerifyCompositionAsync(Action<bool, string> check)
    {
        var original = _appearance;
        var bounds = new Windows.Graphics.RectInt32(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
        var work = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Move(new(work.X + 120, work.Y + 120));
        // A native opaque test rectangle has no independent XAML/compositor
        // startup animation or resource lifetime. It is not application UI.
        var backing = CreateWindowEx(0x08000088, "STATIC", "WinUI composition test backing", 0x90000006,
            AppWindow.Position.X - 30, AppWindow.Position.Y - 30, AppWindow.Size.Width + 60, AppWindow.Size.Height + 60,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero); // SS_WHITERECT
        if (backing == IntPtr.Zero) throw new InvalidOperationException("Cannot create composition test backing");
        UpdateWindow(backing);
        Activate(); NativeWindowStyle.RaiseTopmost(this);
        try
        {
            // Sample only our own test surfaces: a known opaque backing window and
            // the blank area of our caption window. XAML bitmaps cannot prove that
            // the desktop compositor actually lets the backing window show through.
            UpdateAppearance(original with { BackgroundColor = "#000000", BackgroundOpacity = 0 });
            await Task.Delay(180);
            var position = new Point(_root.ActualWidth - 70, _root.ActualHeight - 22);
            var transparent = ReadComposedPixel(position);
            check(transparent.R > 230 && transparent.G > 230 && transparent.B > 230,
                $"Desktop composition: transparent caption reveals white test window ({transparent}; {_windowStyle.DescribePoint(position)})");
            check(_windowStyle.InputHitTest(position) == 1, "Transparent blank surface routes input to XAML for click/drag handling");
            UpdateAppearance(_appearance with { BackgroundOpacity = .5 });
            await Task.Delay(120);
            var half = ReadComposedPixel(position);
            check(half.R is > 85 and < 175 && half.G is > 85 and < 175 && half.B is > 85 and < 175,
                $"Desktop composition: half opacity blends with test window ({half})");
            var edges = new[]
            {
                new Point(3, _root.ActualHeight / 2),
                new Point(_root.ActualWidth - 4, _root.ActualHeight / 2),
                new Point(_root.ActualWidth / 2, 3),
                new Point(_root.ActualWidth / 2, _root.ActualHeight - 4)
            }.Select(ReadComposedPixel).ToArray();
            check(edges.All(c => Math.Abs(c.R - half.R) < 12 && Math.Abs(c.G - half.G) < 12 && Math.Abs(c.B - half.B) < 12),
                "Translucent edges blend like the interior without exposing native frame: " + string.Join(",", edges));
            UpdateAppearance(_appearance with { BackgroundOpacity = 1 });
            await Task.Delay(120);
            var opaque = ReadComposedPixel(position);
            check(opaque.R < 25 && opaque.G < 25 && opaque.B < 25,
                $"Desktop composition: full opacity covers test window ({opaque})");
            check(_windowStyle.InputHitTest(position) == 1, "Opaque blank surface routes input to XAML for click/drag handling");
            check(_windowStyle.InputHitTest(new Point(_root.ActualWidth / 2, 2)) == 12, "Actual input-site top edge resizes");
            check(_windowStyle.InputHitTest(new Point(5, 5)) == 13, "Actual input-site corner resizes diagonally");
        }
        finally { DestroyWindow(backing); UpdateAppearance(original); AppWindow.MoveAndResize(bounds); }

        // Exercise natural rendering instead of only timing property assignments.
        UpdateAppearance(original with { Edge = CaptionEdge.Uniform });
        var times = new List<double>();
        var prefix = string.Concat(Enumerable.Repeat("性能测试字幕 English caption。", 120));
        for (var i = 0; i < 60; i++)
        {
            var watch = Stopwatch.StartNew();
            _caption.SetText(prefix + i);
            await Task.Delay(16);
            times.Add(watch.Elapsed.TotalMilliseconds);
        }
        times.Sort();
        check(times[56] < 100, $"Live rendering under repeated outlined updates: p95 {times[56]:F1} ms");
        var updates = _caption.TextUpdates;
        for (var i = 0; i < 100; i++) _caption.SetText(_caption.Value);
        check(_caption.TextUpdates == updates, "Unchanged captions perform no text layout updates");
        UpdateAppearance(original);

        var entries = Enumerable.Range(0, 3000).Select(i => new TranscriptEntry(Guid.NewGuid(), DateTimeOffset.Now,
            $"历史第 {i} 段：" + new string('测', 200))).ToList();
        var session = new SessionRecord(Guid.NewGuid(), DateTimeOffset.Now, entries);
        var revision = 0;
        var history = new HistoryWindow(() => [session], () => Task.FromResult<string?>(null), () => revision);
        try
        {
            history.Activate(); await Task.Delay(200);
            check(history.RowCount == 3000, "Long history contains all 3000 paragraphs");
            var panel = (ItemsStackPanel)history.HistoryList.ItemsPanelRoot;
            check(panel.Children.Count < 100, $"Long history virtualizes text controls ({panel.Children.Count} realized)");
            var scroll = FindHistoryScroll(history.HistoryList)!;
            scroll.ChangeView(null, scroll.ScrollableHeight * .5, null, true);
            await Task.Delay(250);
            var offset = scroll.VerticalOffset;
            entries[^1] = entries[^1] with { Text = "识别追加到最后一段" };
            revision++; history.Refresh(); await Task.Delay(120);
            check(Math.Abs(scroll.VerticalOffset - offset) < 1, "New speech preserves history reading position");
            var watch = Stopwatch.StartNew();
            for (var i = 0; i < 100; i++) history.Refresh();
            check(watch.Elapsed.TotalMilliseconds < 100, $"Idle history skips full-document rebuilds ({watch.Elapsed.TotalMilliseconds:F1} ms / 100 refreshes)");
        }
        finally { history.Close(); }
    }
    private static ScrollViewer? FindHistoryScroll(DependencyObject root)
    {
        if (root is ScrollViewer scroll) return scroll;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindHistoryScroll(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }
    private Windows.UI.Color ReadComposedPixel(Point point)
    {
        var physical = new PixelPoint { X = (int)(point.X * _windowStyle.Scale), Y = (int)(point.Y * _windowStyle.Scale) };
        ClientToScreen(WinRT.Interop.WindowNative.GetWindowHandle(this), ref physical);
        var dc = GetDC(IntPtr.Zero);
        try
        {
            var value = GetPixel(dc, physical.X, physical.Y);
            if (value == uint.MaxValue) throw new InvalidOperationException("Unable to sample test surface");
            return Windows.UI.Color.FromArgb(255, (byte)value, (byte)(value >> 8), (byte)(value >> 16));
        }
        finally { ReleaseDC(IntPtr.Zero, dc); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct PixelPoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref PixelPoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(IntPtr dc, int x, int y);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateWindowExW")]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string title, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool UpdateWindow(IntPtr window);
}
