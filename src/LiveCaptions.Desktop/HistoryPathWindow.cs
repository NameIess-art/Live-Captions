using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;

namespace LiveCaptions.Desktop;

internal sealed class HistoryPathWindow : Window
{
    internal TextBox PathText { get; } = new() { Header = "当前保存路径", IsReadOnly = true, TextWrapping = TextWrapping.Wrap };
    internal Button ChooseButton { get; } = new() { Content = "选择文件夹…" };
    internal Button OpenButton { get; } = new() { Content = "打开文件夹" };
    internal Button DefaultButton { get; } = new() { Content = "恢复默认" };
    private readonly InfoBar _notice = new() { IsClosable = true };
    private readonly Func<string, Task<string?>> _save;
    private bool _closed, _busy;

    internal HistoryPathWindow(string currentPath, string defaultPath, Func<string, Task<string?>> save)
    {
        Title = "历史保存路径"; _save = save; PathText.Text = currentPath;
        var root = new StackPanel { RequestedTheme = ElementTheme.Dark, Padding = new Thickness(20), Spacing = 16,
            Background = (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"] };
        root.Children.Add(PathText);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(ChooseButton); buttons.Children.Add(OpenButton); buttons.Children.Add(DefaultButton);
        root.Children.Add(buttons);
        root.Children.Add(new TextBlock { Text = "修改后，本次及之后自动保存的历史会写入新目录。已有文件保留在原目录。", TextWrapping = TextWrapping.Wrap });
        root.Children.Add(_notice);
        Content = Ui.Scroll(root);
        var style = new NativeWindowStyle(this) { MinimumWidth = 560, MinimumHeight = 340 };
        style.SetSize(640, 360);
        Closed += (_, _) => _closed = true;
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
        _notice.Message = "保存路径已更新"; _notice.Severity = InfoBarSeverity.Success; _notice.IsOpen = true;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy; ChooseButton.IsEnabled = OpenButton.IsEnabled = DefaultButton.IsEnabled = !busy;
    }

    private void Report(string message)
    {
        if (_closed) return;
        _notice.Message = message; _notice.Severity = InfoBarSeverity.Error; _notice.IsOpen = true;
    }
}
