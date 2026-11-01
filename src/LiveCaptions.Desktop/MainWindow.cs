using System.ComponentModel;
using System.Diagnostics;
using LiveCaptions.Core;
using LiveCaptions.Desktop.Capture;
using LiveCaptions.Desktop.Storage;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace LiveCaptions.Desktop;

public sealed partial class MainWindow : Window
{
    private int _instanceActivations;
    internal void ActivateExistingInstance()
    {
        if (_closing) return;
        _instanceActivations++;
        NativeWindowStyle.Restore(this);
    }

    private readonly Transcript _transcript = new();
    private readonly CaptureClient _capture = new();
    private readonly HistoryStore _store;
    private readonly HistoryPathStore _historyPathStore;
    private readonly DispatcherQueueTimer _timer, _nativeTimer, _appearanceTimer;
    private readonly AppearanceStore _appearanceStore;
    private readonly bool _startCapture;
    private readonly Grid _root = new() { RequestedTheme = ElementTheme.Dark, Background = new SolidColorBrush(Colors.Transparent) };
    private readonly Border _surface = new() { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
        BorderBrush = new SolidColorBrush(CaptionAppearance.ParseColor("#555555")) };
    private readonly CaptionView _caption = new() { Margin = new Thickness(16, 10, 8, 10) };
    private readonly Button _settings = Ui.IconButton("\uE713", "设置");
    private readonly Button _history = Ui.IconButton("\uE81C", "历史");
    private readonly Button _close = Ui.IconButton("\uE8BB", "关闭");
    private readonly Button _dragHandle = Ui.IconButton("\uE7C2", "按住拖动窗口");
    private readonly NativeWindowStyle _windowStyle;
    private HistoryWindow? _historyWindow;
    private CaptionStyleWindow? _styleWindow;
    private HistoryPathWindow? _historyPathWindow;
    private Task<string?>? _historyPathTask;
    private MenuFlyout? _menu;
    private readonly HashSet<MenuFlyout> _openMenus = [];
    private CaptionAppearance _appearance;
    private NativeLanguageSurfaces? _nativeSurfaces;
    private bool _showNativeCaptions, _closing, _allowClose, _connected, _appearanceDirty, _languageBusy, _clearingHistory;
    private string _currentCaption = "";
    private string? _source, _saveError;
    private int _savedRevision = -1;
    private DateTime _lastSave = DateTime.MinValue;
    private Task<bool>? _saveTask;
    private Task<string?>? _clearTask;
    private Task<LanguageSettingsResult>? _languageTask;

    public MainWindow(bool startCapture = true, string? historyRoot = null)
    {
        _startCapture = startCapture;
        _store = new(historyRoot);
        _historyPathStore = new(historyRoot == null
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiveCaptions.Enhanced", "history-settings.json")
            : Path.Combine(historyRoot, "preferences", "history-settings.json"), _store.Root);
        _store.ArchiveRoot = _historyPathStore.Load();
        _appearanceStore = new(historyRoot == null
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiveCaptions.Enhanced", "caption-style.json")
            : Path.Combine(historyRoot, "preferences", "caption-style.json"));
        _appearance = _appearanceStore.Load();
        Title = "实时字幕增强";
        var layout = new Grid();
        layout.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        layout.Children.Add(_caption);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 6, 0) };
        foreach (var button in new[] { _settings, _history, _close })
        {
            button.Width = button.Height = 32;
            ((FontIcon)button.Content).FontSize = 14;
        }
        buttons.Children.Add(_settings); buttons.Children.Add(_history); buttons.Children.Add(_close);
        Grid.SetColumn(buttons, 1); layout.Children.Add(buttons);
        _dragHandle.Width = _dragHandle.Height = 32;
        _dragHandle.CornerRadius = new CornerRadius(4);
        _dragHandle.HorizontalAlignment = HorizontalAlignment.Right;
        _dragHandle.VerticalAlignment = VerticalAlignment.Bottom;
        _dragHandle.Margin = new Thickness(0, 0, 9, 8);
        Grid.SetColumn(_dragHandle, 1); layout.Children.Add(_dragHandle);
        _surface.Child = layout; _root.Children.Add(_surface); Content = _root;
        SystemBackdrop = new TransparentBackdrop();
        _windowStyle = new(this, caption: true);
        ConfigureHandleDrag();
        _windowStyle.SetSize(1000, 90); _windowStyle.PlaceBottom();
        _timer = DispatcherQueue.CreateTimer(); _timer.Interval = TimeSpan.FromMilliseconds(350);
        _timer.Tick += async (_, _) =>
        {
            _historyWindow?.Refresh();
            if (DateTime.UtcNow - _lastSave > TimeSpan.FromSeconds(5) && _savedRevision != _transcript.Revision)
            { _lastSave = DateTime.UtcNow; await PersistAsync(); }
        };
        _nativeTimer = DispatcherQueue.CreateTimer(); _nativeTimer.Interval = TimeSpan.FromMilliseconds(500);
        _nativeTimer.Tick += (_, _) => MaintainNativeVisibility();
        _appearanceTimer = DispatcherQueue.CreateTimer(); _appearanceTimer.Interval = TimeSpan.FromMilliseconds(350);
        _appearanceTimer.IsRepeating = false; _appearanceTimer.Tick += (_, _) => SaveAppearance();
        _settings.Click += (_, _) => { _menu?.Hide(); _menu = CreateSettingsMenu(); _menu.ShowAt(_settings); };
        _history.Click += (_, _) => ShowHistory();
        _close.Click += async (_, _) => await ShutdownAsync();
        _root.Loaded += (_, _) => UpdateMinimumHeight();
        _root.SizeChanged += (_, _) => UpdateMinimumHeight();
        AppWindow.Closing += async (_, e) => { if (!_allowClose) { e.Cancel = true; await ShutdownAsync(); } };
        Closed += (_, _) =>
        {
            CancelHandleDrag();
            _nativeTimer.Stop(); ReleaseNativeSurfaces();
            _historyWindow?.Close(); _styleWindow?.Close(); _historyPathWindow?.Close();
            Application.Current.Exit();
        };
        ApplyAppearance(); ArchivePreviousHistory();
        _capture.Received += OnCaptureReceived;
        if (startCapture)
        {
            EnsureNativeCaptions(); _nativeTimer.Start(); _capture.Start(); _timer.Start();
        }
    }

    private bool IsHandlePoint(Point point)
    {
        var p = _dragHandle.TransformToVisual(_root).TransformPoint(new Point());
        return new Rect(p.X, p.Y, _dragHandle.ActualWidth, _dragHandle.ActualHeight).Contains(point);
    }
    private bool IsBlankPoint(Point point)
    {
        foreach (var button in new[] { _settings, _history, _close, _dragHandle })
        {
            var p = button.TransformToVisual(_root).TransformPoint(new Point());
            if (new Rect(p.X, p.Y, button.ActualWidth, button.ActualHeight).Contains(point)) return false;
        }
        return !_caption.HitText(point, _root);
    }
    private bool IsDragSource(object source)
    {
        for (var node = source as DependencyObject; node != null; node = VisualTreeHelper.GetParent(node))
            if (ReferenceEquals(node, _dragHandle)) return true;
        return false;
    }
    private bool IsBlankSource(object source)
    {
        // Blank clicks may clear selection, but never start moving the window.
        for (var node = source as DependencyObject; node != null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is TextBlock or RichTextBlock || node is Control && !ReferenceEquals(node, _caption.Scroll)) return false;
            if (ReferenceEquals(node, _root)) return true;
        }
        return false;
    }
    private void UpdateMinimumHeight()
    {
        var minimum = Math.Max(86, _caption.LineHeight * 2 + 26);
        _windowStyle.MinimumHeight = minimum;
        if (AppWindow.Size.Height < minimum * _windowStyle.Scale)
        {
            _windowStyle.SetSize(AppWindow.Size.Width / _windowStyle.Scale, minimum);
            _windowStyle.KeepInWorkArea();
        }
    }
    private void ApplyAppearance()
    {
        _surface.Background = _appearance.BackgroundBrush(); _caption.Apply(_appearance);
        UpdateMinimumHeight();
    }
    private string? UpdateAppearance(CaptionAppearance appearance)
    {
        _appearance = appearance.Normalize(); ApplyAppearance(); _appearanceDirty = true;
        _appearanceTimer.Stop(); _appearanceTimer.Start(); return null;
    }
    private void SaveAppearance()
    {
        _appearanceTimer.Stop();
        if (!_appearanceDirty) return;
        try { _appearanceStore.Save(_appearance); _appearanceDirty = false; _styleWindow?.ReportSaveError(""); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AutomationProperties.SetHelpText(_settings, "字幕样式保存失败：" + ex.Message);
            _styleWindow?.ReportSaveError("字幕样式保存失败，请检查本地存储权限。");
        }
    }

    private void EnsureNativeCaptions()
    {
        try
        {
            using var running = NativeCaptionLifetime.FindProcess();
            if (running != null) { AttachNativeProcess((uint)running.Id); return; }
            using var process = Process.Start(new ProcessStartInfo(NativeCaptionLifetime.ExecutablePath)
            { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden, CreateNoWindow = true });
            if (process != null && NativeCaptionLifetime.IsNativeProcess(process)) AttachNativeProcess((uint)process.Id);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException)
        { AutomationProperties.SetHelpText(_caption.Text, "请按 Win + Ctrl + L 打开系统实时字幕。"); }
    }
    private void OnCaptureReceived(CaptureMessage message) => DispatcherQueue.TryEnqueue(() => ProcessCapture(message));
    private void ProcessCapture(CaptureMessage message)
    {
        if (_closing || _languageBusy) return;
        if (message.Status == "connected")
        {
            if (_connected && _source != message.Source) _transcript.Break("原生字幕窗口已更换", DateTimeOffset.Now);
            _connected = true; _source = message.Source;
            if (_startCapture && long.TryParse(message.Source, out var hwnd)) UpdateNativeVisibility(new IntPtr(hwnd));
            _currentCaption = message.Text;
            _caption.SetText(message.Text); _transcript.Accept(message.Text, DateTimeOffset.Now);
            AutomationProperties.SetHelpText(_caption.Text, "");
        }
        else
        {
            if (_connected) _transcript.Break("采集中断", DateTimeOffset.Now);
            _connected = false; _currentCaption = ""; _caption.SetText("");
            AutomationProperties.SetHelpText(_caption.Text, message.Detail);
        }
    }
    private void UpdateNativeVisibility(IntPtr handle)
    {
        if (_showNativeCaptions || _closing) return;
        try
        {
            if (_nativeSurfaces?.Owns(handle) == true) return;
            var replacement = new NativeLanguageSurfaces(handle);
            ReleaseNativeSurfaces(); _nativeSurfaces = replacement;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or Win32Exception)
        { AutomationProperties.SetHelpText(_settings, "原生字幕窗口隐藏失败，将自动重试。"); }
    }
    private void AttachNativeProcess(uint process)
    {
        if (_showNativeCaptions || _closing || _nativeSurfaces?.Owns(process) == true) return;
        try { var replacement = new NativeLanguageSurfaces(process); ReleaseNativeSurfaces(); _nativeSurfaces = replacement; }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or Win32Exception)
        { AutomationProperties.SetHelpText(_settings, "原生字幕窗口隐藏失败，将自动重试。"); }
    }
    private void MaintainNativeVisibility()
    {
        if (_showNativeCaptions) return;
        if (_nativeSurfaces is { IsAlive: false }) ReleaseNativeSurfaces();
        if (_nativeSurfaces != null) { _nativeSurfaces.Refresh(); return; }
        if (!_startCapture || _closing) return;
        using var process = NativeCaptionLifetime.FindProcess();
        if (process != null) AttachNativeProcess((uint)process.Id);
    }
    private void ReleaseNativeSurfaces() { _nativeSurfaces?.Dispose(); _nativeSurfaces = null; }

    internal MenuFlyout CreateSettingsMenu()
    {
        var menu = new MenuFlyout();
        var language = new MenuFlyoutItem { Text = "识别语言", Icon = new FontIcon { Glyph = "\uE774" }, IsEnabled = !_languageBusy };
        language.Click += async (_, _) =>
        {
            ShowLanguageMenu(new(false, Detail: "正在读取语言…"));
            await ChangeLanguageAsync(null);
        };
        var style = new MenuFlyoutItem { Text = "字幕样式", Icon = new FontIcon { Glyph = "\uE790" } };
        style.Click += (_, _) => ShowStyle();
        var historyPath = new MenuFlyoutItem { Text = "历史保存路径", Icon = new FontIcon { Glyph = "\uE8B7" } };
        historyPath.Click += (_, _) => ShowHistoryPath();
        var native = new ToggleMenuFlyoutItem { Text = "显示原生字幕", IsChecked = _showNativeCaptions };
        native.Click += (_, _) =>
        {
            _showNativeCaptions = native.IsChecked;
            if (_showNativeCaptions) ReleaseNativeSurfaces(); else MaintainNativeVisibility();
        };
        menu.Items.Add(language); menu.Items.Add(style); menu.Items.Add(historyPath); menu.Items.Add(new MenuFlyoutSeparator()); menu.Items.Add(native);
        return ObserveMenu(menu);
    }
    private void ShowHistoryPath()
    {
        if (_historyPathWindow != null) { NativeWindowStyle.Restore(_historyPathWindow); return; }
        _historyPathWindow = new(_store.ArchiveRoot, _historyPathStore.DefaultPath, ChangeHistoryPathAsync);
        NativeWindowStyle.SetOwner(_historyPathWindow, this);
        _historyPathWindow.Closed += (_, _) => _historyPathWindow = null;
        _historyPathWindow.Activate();
    }
    private Task<string?> ChangeHistoryPathAsync(string path)
    {
        if (_closing) return Task.FromResult<string?>("应用正在关闭，请下次打开后修改。");
        if (_historyPathTask is { IsCompleted: false }) return Task.FromResult<string?>("正在保存路径，请稍后重试。");
        return _historyPathTask = ChangeHistoryPathCoreAsync(path);
    }
    private async Task<string?> ChangeHistoryPathCoreAsync(string path)
    {
        try
        {
            var saved = await Task.Run(() => _historyPathStore.Save(path));
            _store.ArchiveRoot = saved;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return "保存路径未更新，请检查文件夹是否可写。\n" + ex.Message; }
    }
    private MenuFlyout ObserveMenu(MenuFlyout menu)
    {
        menu.Opening += (_, _) =>
        {
            CancelHandleDrag();
            _openMenus.Add(menu);
            _windowStyle.SetDraggingEnabled(false);
        };
        menu.Closed += (_, _) =>
        {
            _openMenus.Remove(menu);
            _windowStyle.SetDraggingEnabled(_openMenus.Count == 0);
        };
        return menu;
    }
    private void ShowStyle()
    {
        if (_styleWindow != null) { NativeWindowStyle.Restore(_styleWindow); return; }
        _styleWindow = new(_appearance, UpdateAppearance);
        NativeWindowStyle.SetOwner(_styleWindow, this);
        _styleWindow.Closed += (_, _) => { SaveAppearance(); _styleWindow = null; };
        _styleWindow.Activate();
    }
    private void ShowLanguageMenu(LanguageSettingsResult result)
    {
        _menu?.Hide(); _menu = CreateLanguageMenu(result); _menu.ShowAt(_settings);
    }
    internal MenuFlyout CreateLanguageMenu(LanguageSettingsResult result)
    {
        var menu = new MenuFlyout();
        if (!string.IsNullOrEmpty(result.Detail)) menu.Items.Add(new MenuFlyoutItem { Text = result.Detail, IsEnabled = false });
        foreach (var language in (result.Languages ?? []).Where(x => x.Installed == true))
        {
            var item = new ToggleMenuFlyoutItem { Text = language.Name, IsChecked = language.Name == result.Current };
            item.Click += async (_, _) => await ChangeLanguageAsync(language.Name);
            menu.Items.Add(item);
        }
        if (!result.Success && result.Detail != "正在读取语言…")
        {
            var retry = new MenuFlyoutItem { Text = "重试" };
            retry.Click += async (_, _) => await ChangeLanguageAsync(null);
            menu.Items.Add(retry);
        }
        return ObserveMenu(menu);
    }
    private async Task ChangeLanguageAsync(string? language)
    {
        if (_languageBusy) return;
        _languageBusy = true; _settings.IsEnabled = false;
        try
        {
            _languageTask = NativeLanguageSettings.RunAsync(language);
            var result = await _languageTask;
            if (_closing) return;
            if (result.Success && language != null)
            { _transcript.Break("识别语言已更换", DateTimeOffset.Now); _menu?.Hide(); }
            else ShowLanguageMenu(result);
        }
        finally { _languageBusy = false; _languageTask = null; _settings.IsEnabled = true; }
    }

    private void ArchivePreviousHistory()
    {
        try
        {
            _store.ArchivePending();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { AutomationProperties.SetHelpText(_history, "上次历史归档失败，原文件已保留，退出时将重试：" + ex.Message); }
    }
    private IReadOnlyList<SessionRecord> GetHistorySessions() => [_transcript.Snapshot()];
    private void ShowHistory()
    {
        if (_historyWindow != null) { _historyWindow.Refresh(true); NativeWindowStyle.Restore(_historyWindow); return; }
        _historyWindow = new(GetHistorySessions, ClearHistoryAsync, () => _transcript.Revision);
        NativeWindowStyle.SetOwner(_historyWindow, this);
        _historyWindow.Closed += (_, _) => _historyWindow = null;
        _historyWindow.Activate();
    }
    private Task<bool> PersistAsync()
    {
        if (_clearingHistory) return Task.FromResult(true);
        if (_saveTask is { IsCompleted: false }) return _saveTask;
        return _saveTask = SaveSnapshotAsync(_transcript.Snapshot(), _transcript.Revision);
    }
    private async Task<bool> SaveSnapshotAsync(SessionRecord snapshot, int revision)
    {
        try { await Task.Run(() => _store.Save(snapshot)); _savedRevision = revision; _saveError = null; return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _saveError = ex.Message; AutomationProperties.SetHelpText(_history, "历史保存失败：" + ex.Message); return false; }
    }
    private Task<string?> ClearHistoryAsync()
    {
        if (_closing) return Task.FromResult<string?>("应用正在关闭，请重新打开后再清空历史。");
        if (_clearTask is { IsCompleted: false }) return _clearTask;
        return _clearTask = ClearHistoryCoreAsync();
    }
    private async Task<string?> ClearHistoryCoreAsync()
    {
        _clearingHistory = true;
        try
        {
            if (_saveTask != null) await _saveTask;
            await Task.Run(_store.Clear); _transcript.Clear(_currentCaption);
            _savedRevision = _transcript.Revision; _lastSave = DateTime.UtcNow; _saveError = null;
            _historyWindow?.Refresh(true); return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _savedRevision = -1; return "清空历史未完成，请检查文件权限后重试。\n" + ex.Message; }
        finally { _clearingHistory = false; }
    }

    private async Task<bool> ArchiveHistoryAsync()
    {
        if (!await PersistAsync()) return false;
        try
        {
            await Task.Run(_store.ArchivePending);
            _transcript.Clear(_currentCaption);
            _savedRevision = _transcript.Revision;
            _historyWindow?.Refresh(true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _saveError = ex.Message; return false; }
    }

    internal async Task ShutdownAsync()
    {
        if (_closing) return;
        _closing = true; _timer.Stop(); _menu?.Hide(); SaveAppearance();
        CancelHandleDrag();
        if (_historyPathTask != null) await _historyPathTask;
        if (_languageTask != null) await _languageTask;
        if (_clearTask != null) await _clearTask;
        if (_saveTask != null) await _saveTask;
        if (!await ArchiveHistoryAsync())
        {
            await Ui.Dialog(this, "历史归档失败", "历史尚未完成保存和清空，请检查存储权限后再次关闭。原记录已保留。\n" + _saveError);
            _closing = false; if (_startCapture) _timer.Start(); return;
        }
        _capture.Received -= OnCaptureReceived; await _capture.DisposeAsync();
        if (_startCapture) await NativeCaptionLifetime.CloseAsync();
        _nativeTimer.Stop(); ReleaseNativeSurfaces(); NativeLanguageSurfaces.RestoreAll();
        _allowClose = true; Close();
    }
}
