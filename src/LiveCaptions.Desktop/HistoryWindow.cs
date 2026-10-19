using System.Runtime.InteropServices;
using System.Text;
using System.Collections.ObjectModel;
using System.ComponentModel;
using LiveCaptions.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Markup;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace LiveCaptions.Desktop;

public sealed class HistoryWindow : Window
{
    private readonly Func<IReadOnlyList<SessionRecord>> _getSessions;
    private readonly Func<Task<string?>> _clear;
    private readonly Func<int> _getRevision;
    private readonly ObservableCollection<HistoryRow> _rows = [];
    private int _revision = -1;
    private DateTime _scrollingUntil;
    private ScrollViewer? _scroll;
    private bool _clearing, _dialogOpen;
    internal ListView HistoryList { get; } = new()
    {
        SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = false,
        Padding = new Thickness(0),
        HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch
    };
    // Used by selection regression checks after the first row is realized.
    internal TextBox HistoryText => FindText(HistoryList.ContainerFromIndex(0)) ?? throw new InvalidOperationException("History row not realized");
    internal int RowCount => _rows.Count;
    internal Button CopyButton { get; } = new() { Content = "复制", MinWidth = 80 };
    internal Button ExportButton { get; } = new() { Content = "导出", MinWidth = 80 };
    internal Button ClearButton { get; } = Ui.IconButton("\uE74D", "清空历史");
    private readonly InfoBar _notice = new() { IsClosable = true };

    internal HistoryWindow(Func<IReadOnlyList<SessionRecord>> getSessions, Func<Task<string?>> clear, Func<int> getRevision)
    {
        _getSessions = getSessions; _clear = clear; _getRevision = getRevision; Title = "字幕历史";
        var root = new Grid { RequestedTheme = ElementTheme.Dark, Padding = new Thickness(20), RowSpacing = 12,
            Background = (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"] };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var toolbar = new Grid();
        toolbar.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        toolbar.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        toolbar.Children.Add(new TextBlock { Text = "字幕历史", Style = (Style)Application.Current.Resources["TitleTextBlockStyle"],
            VerticalAlignment = VerticalAlignment.Center });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(CopyButton); actions.Children.Add(ExportButton); actions.Children.Add(ClearButton);
        Grid.SetColumn(actions, 1); toolbar.Children.Add(actions); root.Children.Add(toolbar);
        Grid.SetRow(_notice, 1); root.Children.Add(_notice);
        HistoryList.ItemsSource = _rows;
        HistoryList.ItemTemplate = (DataTemplate)XamlReader.Load("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <TextBox Text="{Binding Text, Mode=OneWay}" IsReadOnly="True" AcceptsReturn="True"
                       TextWrapping="Wrap" FontSize="16" BorderThickness="0" Background="Transparent"
                       Padding="8,2" HorizontalAlignment="Stretch" />
            </DataTemplate>
            """);
        HistoryList.ItemContainerStyle = (Style)XamlReader.Load("""
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="ListViewItem">
              <Setter Property="HorizontalContentAlignment" Value="Stretch" />
              <Setter Property="Padding" Value="0" />
              <Setter Property="MinHeight" Value="0" />
            </Style>
            """);
        ScrollViewer.SetVerticalScrollBarVisibility(HistoryList, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(HistoryList, ScrollBarVisibility.Disabled);
        AutomationProperties.SetName(HistoryList, "全部字幕历史");
        HistoryList.Loaded += (_, _) =>
        {
            _scroll = FindScroll(HistoryList);
            if (_scroll != null) _scroll.ViewChanged += (_, e) =>
            { if (e.IsIntermediate) _scrollingUntil = DateTime.UtcNow.AddMilliseconds(200); };
        };
        HistoryList.AddHandler(UIElement.PointerWheelChangedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => _scrollingUntil = DateTime.UtcNow.AddMilliseconds(200)), true);
        Grid.SetRow(HistoryList, 2); root.Children.Add(HistoryList); Content = root;
        var style = new NativeWindowStyle(this) { MinimumWidth = 560, MinimumHeight = 360 };
        style.SetSize(800, 600);
        CopyButton.Click += async (_, _) =>
        {
            try
            {
                var data = new DataPackage(); data.SetText(AllText()); Clipboard.SetContent(data);
                _notice.Message = "已复制全部历史"; _notice.Severity = InfoBarSeverity.Success; _notice.IsOpen = true;
            }
            catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
            { await Ui.Dialog(this, "复制", "剪贴板正忙，请重试。"); }
        };
        ExportButton.Click += Export;
        ClearButton.Click += Clear;
        root.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Escape && !_dialogOpen) { Close(); e.Handled = true; } };
        Refresh(true);
    }
    internal string AllText() => HistoryDocument.PlainText(_getSessions());
    internal string AllMarkdown() => HistoryDocument.Markdown(_getSessions());
    internal void Refresh(bool force = false)
    {
        CopyButton.IsEnabled = ExportButton.IsEnabled = ClearButton.IsEnabled = _rows.Count > 0 && !_clearing;
        var revision = _getRevision();
        if (!force && (_revision == revision || DateTime.UtcNow < _scrollingUntil || HasSelection())) return;
        var tail = _rows.Count > 0 && _scroll != null && _scroll.VerticalOffset >= _scroll.ScrollableHeight - 1;
        // Bound each text layout, and let ListView's ItemsStackPanel virtualize old
        // rows. Speech updates change only the active row, never the full document.
        var index = 0;
        foreach (var entry in _getSessions().SelectMany(x => x.Entries).Where(x => !x.IsNotice))
        {
            for (var offset = 0; offset < entry.Text.Length; offset += 1200)
            {
                var text = entry.Text.Substring(offset, Math.Min(1200, entry.Text.Length - offset));
                if (index == _rows.Count) _rows.Add(new(text));
                else _rows[index].Text = text;
                index++;
            }
        }
        while (_rows.Count > index) _rows.RemoveAt(_rows.Count - 1);
        _revision = revision;
        CopyButton.IsEnabled = ExportButton.IsEnabled = ClearButton.IsEnabled = _rows.Count > 0 && !_clearing;
        if (tail && !force && _rows.Count > 0) HistoryList.ScrollIntoView(_rows[^1]);
    }
    private bool HasSelection()
    {
        // Only inspect realized containers, not all saved history.
        if (HistoryList.ItemsPanelRoot is not ItemsStackPanel panel) return false;
        for (var i = Math.Max(0, panel.FirstCacheIndex); i <= panel.LastCacheIndex && i < _rows.Count; i++)
            if (FindText(HistoryList.ContainerFromIndex(i)) is { SelectionLength: > 0 }) return true;
        return false;
    }
    private static TextBox? FindText(DependencyObject? root)
    {
        if (root is TextBox text) return text;
        if (root == null) return null;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindText(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }
    private sealed class HistoryRow(string text) : INotifyPropertyChanged
    {
        private string _text = text;
        public string Text
        {
            get => _text;
            set { if (_text == value) return; _text = value; PropertyChanged?.Invoke(this, new(nameof(Text))); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
    private async void Clear(object sender, RoutedEventArgs e)
    {
        if (_clearing || _dialogOpen || AllText().Length == 0) return;
        _dialogOpen = true;
        try
        {
            if (await Ui.Dialog(this, "清空历史", "本机已保存的历史和当前会话历史将被删除，无法撤销。需要保留内容时请先导出。", "清空")
                != ContentDialogResult.Primary) return;
            var error = await ClearConfirmedAsync();
            if (error != null) await Ui.Dialog(this, "清空失败", error);
        }
        finally { _dialogOpen = false; }
    }
    internal async Task<string?> ClearConfirmedAsync()
    {
        if (_clearing) return null;
        _clearing = true; Refresh();
        try { return await _clear(); }
        finally { _clearing = false; Refresh(true); }
    }
    private async void Export(object sender, RoutedEventArgs e)
    {
        if (_dialogOpen || AllText().Length == 0) return;
        _dialogOpen = true;
        try
        {
            var picker = new FileSavePicker { SuggestedFileName = $"字幕历史-{DateTime.Now:yyyyMMdd-HHmmss}" };
            picker.FileTypeChoices.Add("Markdown", new List<string> { ".md" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var file = await picker.PickSaveFileAsync();
            if (file == null) return;
            await File.WriteAllTextAsync(file.Path, AllMarkdown(), new UTF8Encoding(false));
            _notice.Message = "已导出 Markdown"; _notice.Severity = InfoBarSeverity.Success; _notice.IsOpen = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ExternalException)
        { await Ui.Dialog(this, "导出失败", ex.Message); }
        finally { _dialogOpen = false; }
    }
    private static ScrollViewer? FindScroll(DependencyObject root)
    {
        if (root is ScrollViewer scroll) return scroll;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindScroll(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }
}
