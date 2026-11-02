using System.Runtime.InteropServices;
using LiveCaptions.Desktop.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;

namespace LiveCaptions.Desktop;

internal sealed class PreferencesWindow : Window
{
    internal ToggleSwitch AutoSaveInput { get; } = new() { Header = "自动保存历史", OnContent = "开", OffContent = "关" };
    internal ToggleSwitch StartupInput { get; } = new() { Header = "开机自启", OnContent = "开", OffContent = "关" };
    internal TextBox PathText { get; } = new() { Header = "历史保存路径", IsReadOnly = true, TextWrapping = TextWrapping.Wrap };
    internal Button ChooseButton { get; } = new() { Content = "选择文件夹…" };
    internal Button OpenButton { get; } = new() { Content = "打开文件夹" };
    internal Button DefaultButton { get; } = new() { Content = "恢复默认" };
    internal InfoBar Notice { get; } = new() { IsClosable = true };
    private readonly Func<string, Task<string?>> _save;
    private readonly Func<bool, Task<string?>> _saveAutoHistory;
    private readonly StartupRegistration _startup;
    private bool _closed, _busy, _updating, _autoSaveHistory, _startupEnabled, _canReadStartup = true;

    internal PreferencesWindow(string currentPath, string defaultPath, Func<string, Task<string?>> save,
        bool autoSaveHistory, Func<bool, Task<string?>> saveAutoHistory, StartupRegistration startup)
    {
        Title = "首选项"; _save = save; PathText.Text = currentPath;
        _saveAutoHistory = saveAutoHistory; _startup = startup;
        _autoSaveHistory = AutoSaveInput.IsOn = autoSaveHistory;
        var root = new StackPanel { RequestedTheme = ElementTheme.Dark, Padding = new Thickness(20), Spacing = 16,
            Background = (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"] };
        root.Children.Add(AutoSaveInput);
        root.Children.Add(new TextBlock { Text = "关闭后，历史仅保留在本次运行中，不自动保存；仍可手动复制或导出。", TextWrapping = TextWrapping.Wrap });
        root.Children.Add(PathText);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(ChooseButton); buttons.Children.Add(OpenButton); buttons.Children.Add(DefaultButton);
        root.Children.Add(buttons);
        root.Children.Add(new TextBlock { Text = "修改后，本次及之后自动保存的历史会写入新目录。已有文件保留在原目录。", TextWrapping = TextWrapping.Wrap });
        root.Children.Add(StartupInput);
        ToolTipService.SetToolTip(StartupInput, "登录 Windows 后自动启动实时字幕增强");
        try { _startupEnabled = StartupInput.IsOn = _startup.IsEnabled; }
        catch (Exception ex) when (IsStartupError(ex))
        {
            _canReadStartup = StartupInput.IsEnabled = false;
            Report("无法读取开机自启设置：" + ex.Message);
        }
        root.Children.Add(Notice);
        Content = Ui.Scroll(root);
        var style = new NativeWindowStyle(this) { MinimumWidth = 560, MinimumHeight = 480 };
        style.SetSize(640, 540);
        Closed += (_, _) => _closed = true;
        AutoSaveInput.Toggled += async (_, _) => { if (!_updating) await ApplyAutoSaveAsync(AutoSaveInput.IsOn); };
        StartupInput.Toggled += (_, _) => { if (!_updating) ApplyStartup(StartupInput.IsOn); };
        ChooseButton.Click += async (_, _) =>
        {
            if (_busy) return;
            SetBusy(true);
            try
            {
                var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                var folder = await picker.PickSingleFolderAsync();
                if (!_closed && folder != null) await ApplyPathAsync(folder.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ExternalException)
            { Report(ex.Message); }
            finally { if (!_closed) SetBusy(false); }
        };
        DefaultButton.Click += async (_, _) =>
        {
            if (_busy) return;
            SetBusy(true);
            try { await ApplyPathAsync(defaultPath); }
            finally { if (!_closed) SetBusy(false); }
        };
        OpenButton.Click += async (_, _) =>
        {
            if (_busy) return;
            SetBusy(true);
            try
            {
                Directory.CreateDirectory(PathText.Text);
                if (!await Windows.System.Launcher.LaunchFolderPathAsync(PathText.Text)) Report("无法打开文件夹，请检查目录是否可用。");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ExternalException) { Report(ex.Message); }
            finally { if (!_closed) SetBusy(false); }
        };
    }

    internal async Task ApplyPathAsync(string path)
    {
        var error = await _save(path);
        if (_closed) return;
        if (error != null) { Report(error); return; }
        PathText.Text = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        Notice.Message = "保存路径已更新"; Notice.Severity = InfoBarSeverity.Success; Notice.IsOpen = true;
    }

    internal async Task ApplyAutoSaveAsync(bool enabled)
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            var error = await _saveAutoHistory(enabled);
            if (error == null) _autoSaveHistory = enabled;
            _updating = true; AutoSaveInput.IsOn = _autoSaveHistory; _updating = false;
            if (error != null) Report(error);
            else if (!_closed)
            { Notice.Message = enabled ? "自动保存历史已开启" : "自动保存历史已关闭"; Notice.Severity = InfoBarSeverity.Success; Notice.IsOpen = true; }
        }
        finally { if (!_closed) SetBusy(false); }
    }

    internal string? ApplyStartup(bool enabled)
    {
        string? error = null;
        try { _startup.SetEnabled(enabled); _startupEnabled = enabled; }
        catch (Exception ex) when (IsStartupError(ex)) { error = "无法更新开机自启，请检查系统权限后重试。\n" + ex.Message; }
        _updating = true; StartupInput.IsOn = _startupEnabled; _updating = false;
        if (error != null) Report(error);
        else { Notice.Message = enabled ? "开机自启已开启" : "开机自启已关闭"; Notice.Severity = InfoBarSeverity.Success; Notice.IsOpen = true; }
        return error;
    }

    private static bool IsStartupError(Exception ex) => ex is IOException or UnauthorizedAccessException or
        System.Security.SecurityException or InvalidOperationException or ArgumentException or NotSupportedException;

    private void SetBusy(bool busy)
    {
        _busy = busy; ChooseButton.IsEnabled = OpenButton.IsEnabled = DefaultButton.IsEnabled = !busy;
        AutoSaveInput.IsEnabled = !busy; StartupInput.IsEnabled = !busy && _canReadStartup;
    }

    private void Report(string message)
    {
        if (_closed) return;
        Notice.Message = message; Notice.Severity = InfoBarSeverity.Error; Notice.IsOpen = true;
    }
}
