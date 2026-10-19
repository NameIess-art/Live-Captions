using System.Text.Json;
using LiveCaptions.Desktop.Capture;
using LiveCaptions.Desktop.Storage;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace LiveCaptions.Desktop;

public sealed partial class MainWindow
{
    internal async Task VerifyUiAsync(string reportPath)
    {
        // Fail closed before any history write or clear. Never test against real user data.
        var testRoot = Path.GetFullPath(_store.Root);
        var reportRoot = Path.GetFullPath(Path.GetDirectoryName(reportPath)!);
        if (!string.Equals(Path.GetDirectoryName(testRoot), reportRoot, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(testRoot).StartsWith("winui-test-", StringComparison.Ordinal) ||
            Directory.Exists(testRoot))
            throw new InvalidOperationException("UI tests require a fresh isolated winui-test-* directory beside the report.");
        var checks = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("WinUI regression failed: " + name);
            checks.Add(name);
        }
        await Task.Delay(250);
        _root.UpdateLayout();
        Check(_root.XamlRoot != null, "WinUI XAML root loaded");
        Check(typeof(Window).Assembly.GetName().Name == "Microsoft.WinUI", "All windows use Microsoft WinUI");
        Check(AppWindow.Presenter is OverlappedPresenter { IsAlwaysOnTop: true, IsMaximizable: false }, "Topmost, resizing allowed, maximize disabled");
        Check(_caption.Text.IsTextSelectionEnabled, "Live captions selectable");
        Check(_caption.Scroll.VerticalScrollBarVisibility == ScrollBarVisibility.Hidden, "Live scrollbar hidden");
        Check(new[] { _settings, _history, _close }.All(x => AutomationProperties.GetName(x).Length > 0), "Icon buttons have accessible names");
        Check(CreateSettingsMenu().Items.Count == 4, "WinUI settings menu contains all entries");
        var languages = CreateLanguageMenu(new(true, "中文", [new("中文", true), new("English", true), new("未安装", false)]));
        Check(languages.Items.Count == 2 && ((ToggleMenuFlyoutItem)languages.Items[0]).IsChecked, "Language menu filters installed languages and marks current");
        ProcessCapture(new("connected", "第一句字幕", "test"));
        ProcessCapture(new("connected", "第一句字幕。第二句字幕", "test"));
        Check(_caption.Value == "第一句字幕。第二句字幕", "Capture updates full live buffer");
        await Task.Delay(40);
        _root.UpdateLayout();
        var run = (Microsoft.UI.Xaml.Documents.Run)((Microsoft.UI.Xaml.Documents.Paragraph)_caption.Text.Blocks[0]).Inlines[0];
        var from = run.ContentStart.GetPositionAtOffset(0, Microsoft.UI.Xaml.Documents.LogicalDirection.Forward);
        var to = from.GetPositionAtOffset(3, Microsoft.UI.Xaml.Documents.LogicalDirection.Forward);
        _caption.Text.Select(from, to);
        Check(_caption.Text.SelectedText == "第一句", "Live text range can be selected: " + _caption.Text.SelectedText);
        _caption.SetText("前缀第一句字幕。第二句字幕");
        Check(_caption.Text.SelectedText == "第一句", "Live selection survives moving native buffer: " + _caption.Text.SelectedText);
        await VerifyCaptionSelectionAsync(Check);
        _caption.Text.Select(_caption.Text.ContentStart, _caption.Text.ContentStart);
        Check(_windowStyle.HasResizeFrame, "Borderless caption window retains native resize frame");
        Check(_windowStyle.IsFullyClient, "No non-client strip above the caption surface");
        var glyph = run.ContentStart.GetPositionAtOffset(1, Microsoft.UI.Xaml.Documents.LogicalDirection.Forward).GetCharacterRect(Microsoft.UI.Xaml.Documents.LogicalDirection.Forward);
        var glyphPoint = _caption.Text.TransformToVisual(_root).TransformPoint(new Windows.Foundation.Point(glyph.X + glyph.Width / 2, glyph.Y + glyph.Height / 2));
        Check(_windowStyle.HitTest(glyphPoint) == 1, "Native text hit test permits selection " + glyph + " / " + glyphPoint);
        var lineEnd = _caption.Text.TransformToVisual(_root).TransformPoint(
            new Windows.Foundation.Point(_caption.Text.ActualWidth - 12, glyph.Y + glyph.Height / 2));
        Check(!IsDraggable(lineEnd) && _windowStyle.HitTest(lineEnd) == 1,
            "Clicking text-area whitespace keeps selection input instead of dragging");
        Check(!IsDragSource(_caption.Text) && !IsDragSource(_settings) && IsDragSource(_surface) && !IsDragSource(new Grid()),
            "Window drag accepts only this window's bare surfaces and excludes text/controls");
        var buttonPoint = _settings.TransformToVisual(_root).TransformPoint(new Windows.Foundation.Point(20, 20));
        Check(_windowStyle.HitTest(buttonPoint) == 1, "Native button hit test permits clicking");
        var blankPoint = new Windows.Foundation.Point(_root.ActualWidth - 50, _root.ActualHeight - 20);
        var blankScreen = new Windows.Graphics.PointInt32(AppWindow.Position.X + (int)Math.Round(blankPoint.X * _windowStyle.Scale),
            AppWindow.Position.Y + (int)Math.Round(blankPoint.Y * _windowStyle.Scale));
        Check(_windowStyle.HitTest(blankPoint) == 1, "Native blank hit test preserves XAML selection and pointer handling");
        Check(_windowStyle.HitTest(new Windows.Foundation.Point(_root.ActualWidth / 2, 2)) == 12, "Native top edge starts resize");
        var input = Microsoft.UI.Input.InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
        Check((input.GetRegionRects(Microsoft.UI.Input.NonClientRegionKind.Caption)?.Length ?? 0) == 0,
            "WinUI input-site has no immediate native caption drag region");
        Check(input.GetRegionRects(Microsoft.UI.Input.NonClientRegionKind.TopBorder).Length > 0,
            "WinUI input-site has native resize regions");
        VerifyBlankPress(Check, blankPoint, glyphPoint);
        foreach (var menu in new[] { CreateSettingsMenu(), languages })
        {
            try
            {
                Check(StartBlankPress(_surface, blankPoint, blankScreen), "Blank press can be active before menu opens");
                menu.ShowAt(_settings); await Task.Delay(100);
                Check(!_windowStyle.IsDragEnabled && (input.GetRegionRects(Microsoft.UI.Input.NonClientRegionKind.Caption)?.Length ?? 0) == 0 &&
                    _windowStyle.HitTest(new Windows.Foundation.Point(_root.ActualWidth - 50, _root.ActualHeight - 20)) == 1,
                    "Open menu suspends both native and XAML window dragging");
                Check(!_blankPressActive && !StartBlankPress(_surface, blankPoint, blankScreen), "Menu cancels active blank press and rejects new window gestures");
                Check(menu.Items.All(item => !IsDragSource(item)), "Menu item text/controls never become a window drag source");
            }
            finally { menu.Hide(); }
            await Task.Delay(100);
            Check(_windowStyle.IsDragEnabled && (input.GetRegionRects(Microsoft.UI.Input.NonClientRegionKind.Caption)?.Length ?? 0) == 0,
                "Closing menu restores blank press gestures without native dragging bypass");
        }
        Check(GetHistorySessions().Count > 0, "Live captures feed transcript");
        Check(await PersistAsync(), "Session writes to disk");
        Check(_store.Load().Sessions.Count == 1, "Saved session reloads");

        ShowHistory(); await Task.Delay(150);
        Check(_historyWindow != null && _historyWindow.Content is Grid, "Independent WinUI history window");
        var history = _historyWindow!;
        Check(history.AllText().Contains("第一句字幕", StringComparison.Ordinal), "History contains captured text");
        Check(history.CopyButton.IsEnabled && history.ExportButton.IsEnabled && history.ClearButton.IsEnabled, "History actions enabled when nonempty");
        history.HistoryText.Select(0, 2);
        var selected = history.HistoryText.SelectedText;
        Check(history.AllText().Length > selected.Length, "Copy/export source ignores selection");
        var before = history.HistoryText.Text;
        ProcessCapture(new("connected", "第一句字幕。第二句字幕。新增内容", "test"));
        history.Refresh();
        Check(history.HistoryText.Text == before && history.HistoryText.SelectedText == selected, "History selection preserved during capture");
        history.Refresh(true);
        Check(history.AllMarkdown().Contains("#", StringComparison.Ordinal), "Markdown export includes document structure");

        ShowStyle(); await Task.Delay(150);
        Check(_styleWindow != null, "Independent WinUI style window");
        var style = _styleWindow!;
        var styleSize = style.AppWindow.Size;
        Check(style.Title.Length == 0 && style.Sections.Items.Select(x => x.Text).SequenceEqual(new[] { "文字", "背景" }),
            "Style window has text/background navigation without a duplicated title");
        Check(style.AppWindow.Presenter is OverlappedPresenter { IsResizable: false, IsMaximizable: false },
            "Style window has fixed dimensions");
        var textOptions = (StackPanel)style.TextPage.Content;
        Check(textOptions.Children.OfType<FrameworkElement>().All(x =>
            x.TransformToVisual(style.TextPage).TransformPoint(new()).Y + x.ActualHeight <= style.TextPage.ActualHeight + 1),
            "All text options fit the fixed page without scrolling");
        await SnapshotAsync((FrameworkElement)style.Content, Path.Combine(Path.GetDirectoryName(reportPath)!, "winui-style-text.png"));
        style.SizeInput.Value = 36;
        Check(_appearance.FontSize == 36, "NumberBox applies custom font size");
        style.Fonts.SelectedIndex = 2;
        Check(_appearance.Font == "mono-sans", "ComboBox applies font family");
        style.Edges.SelectedIndex = 3;
        Check(_appearance.Edge == CaptionEdge.Uniform, "Text edge effect applies");
        style.Sections.SelectedItem = style.Sections.Items[1];
        await Task.Delay(60);
        Check(style.TextPage.Visibility == Visibility.Collapsed && style.BackgroundPage.Visibility == Visibility.Visible &&
            style.AppWindow.Size.Width == styleSize.Width && style.AppWindow.Size.Height == styleSize.Height,
            "Background page switches without resizing the window");
        var backgroundOptions = (StackPanel)style.BackgroundPage.Content;
        Check(backgroundOptions.Children.OfType<FrameworkElement>().All(x =>
            x.TransformToVisual(style.BackgroundPage).TransformPoint(new()).Y + x.ActualHeight <= style.BackgroundPage.ActualHeight + 1),
            "All background options fit the fixed page without scrolling");
        await SnapshotAsync((FrameworkElement)style.Content, Path.Combine(Path.GetDirectoryName(reportPath)!, "winui-style-background.png"));
        style.OpacityInput.Value = 25;
        Check(Math.Abs(_appearance.BackgroundOpacity - .25) < .001, "Slider applies background opacity");
        Check(_surface.Background is SolidColorBrush { Color.A: 64 } && _caption.Text.Opacity == 1, "Background alpha independent of text");
        style.OpacityInput.Value = 0;
        Check(_surface.Background is SolidColorBrush { Color.A: 0 }, "Zero opacity produces fully transparent background");
        style.OpacityInput.Value = 25;
        SaveAppearance();
        Check(_appearanceStore.Load() == _appearance, "Appearance settings roundtrip");
        style.Sections.SelectedItem = style.Sections.Items[0];
        await Task.Delay(60);
        Check(style.TextPage.Visibility == Visibility.Visible && style.BackgroundPage.Visibility == Visibility.Collapsed &&
            style.SizeInput.Value == 36 && style.Fonts.SelectedIndex == 2 && style.Edges.SelectedIndex == 3,
            "Text page retains edited controls after switching pages");
        style.OpenTextPalette(); await Task.Delay(100);
        style.TextPicker.Color = CaptionAppearance.ParseColor("#00FFFF");
        Check(_appearance.TextColor == "#00FFFF", "Native WinUI color picker applies text color");
        foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(((FrameworkElement)style.Content).XamlRoot)) popup.IsOpen = false;
        var confirmation = Ui.CreateDialog(history, "清空历史", "本机已保存的历史和当前会话历史将被删除，无法撤销。", "清空");
        var shown = confirmation.ShowAsync(); await Task.Delay(150);
        Check(confirmation.DefaultButton == ContentDialogButton.Close && confirmation.CloseButtonText == "取消", "Destructive WinUI dialog defaults to cancel");
        await SnapshotAsync(confirmation, Path.Combine(Path.GetDirectoryName(reportPath)!, "winui-confirmation.png"));
        confirmation.Hide(); await shown;
        style.SizeInput.Value = 96;
        Check(_windowStyle.MinimumHeight >= _caption.LineHeight * 2 + 24, "Largest font keeps two complete lines");
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        Check(AppWindow.Position.Y + AppWindow.Size.Height <= work.Y + work.Height, "Growing font stays inside monitor work area");
        style.SizeInput.Value = 36;

        var sample = string.Join("\n", Enumerable.Range(1, 18).Select(i => $"第 {i} 行完整字幕 · caption line {i}"));
        foreach (var size in new[] { (420d, 120d), (720d, 180d), (1000d, 300d), (560d, 155d) })
        {
            _windowStyle.SetSize(size.Item1, Math.Max(size.Item2, _windowStyle.MinimumHeight));
            _caption.SetText(sample);
            await Task.Delay(100); _root.UpdateLayout(); _caption.FollowTail(); await Task.Delay(60);
            var lines = _caption.Scroll.ActualHeight / _caption.LineHeight;
            Check(Math.Abs(lines - Math.Round(lines)) < .05, $"Whole-line viewport at {size}");
            Check(Math.Abs(_caption.Scroll.VerticalOffset - _caption.Scroll.ScrollableHeight) < 1, $"Live follows newest line at {size}");
        }
        _windowStyle.SetSize(1000, 150);
        UpdateAppearance(new()); _caption.SetText(sample);
        await Task.Delay(100);
        await SnapshotAsync(_root, Path.Combine(Path.GetDirectoryName(reportPath)!, "winui-caption.png"));
        await SnapshotAsync((FrameworkElement)history.Content, Path.Combine(Path.GetDirectoryName(reportPath)!, "winui-history.png"));
        await SnapshotAsync((FrameworkElement)style.Content, Path.Combine(Path.GetDirectoryName(reportPath)!, "winui-style.png"));
        Check(true, "Three WinUI windows rendered to PNG");

        Check(await history.ClearConfirmedAsync() == null, "Clear waits for persistence and succeeds");
        Check(_store.Load().Sessions.Count == 0 && history.AllText().Length == 0, "Clear removes saved and current history");
        Check(!history.CopyButton.IsEnabled && !history.ExportButton.IsEnabled && !history.ClearButton.IsEnabled, "Empty history actions disabled");
        Check(_caption.Value.Length > 0, "Clear leaves live display intact");
        ProcessCapture(new("connected", "第一句字幕。第二句字幕。新增内容", "test"));
        Check(history.AllText().Length == 0, "Cleared native buffer does not refill history");
        ProcessCapture(new("connected", "第一句字幕。第二句字幕。新增内容。后续新字幕", "test"));
        Check(history.AllText().Contains("后续新字幕", StringComparison.Ordinal), "New text recorded after clearing");
        Check(await PersistAsync(), "Post-clear history persists");
        ProcessCapture(new("error", Detail: "测试重连"));
        Check(AutomationProperties.GetHelpText(_caption.Text) == "测试重连", "Capture error exposed to accessibility");
        ProcessCapture(new("connected", "重新连接", "new-source"));
        Check(_caption.Value == "重新连接", "Capture reconnect recovers");

        history.Close(); style.Close();
        await VerifyMonitorFrameAsync(Check);
        await VerifyCompositionAsync(Check);
        await NativeSurfaceRegression.VerifyAsync(Check);

        UpdateAppearance(new()); SaveAppearance();
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new { Framework = "WinUI 3", Passed = checks.Count, Checks = checks },
            new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task SnapshotAsync(FrameworkElement element, string path)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(element);
        if (bitmap.PixelWidth == 0 || bitmap.PixelHeight == 0) throw new InvalidOperationException("Empty WinUI rendering");
        var buffer = await bitmap.GetPixelsAsync();
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        using var reader = DataReader.FromBuffer(buffer);
        var bytes = new byte[buffer.Length]; reader.ReadBytes(bytes);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth,
            (uint)bitmap.PixelHeight, 96, 96, bytes);
        await encoder.FlushAsync();
        stream.Seek(0);
        using var input = stream.GetInputStreamAt(0);
        using var data = new DataReader(input);
        await data.LoadAsync((uint)stream.Size);
        var png = new byte[stream.Size]; data.ReadBytes(png);
        await File.WriteAllBytesAsync(path, png);
    }
}
