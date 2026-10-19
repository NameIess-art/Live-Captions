using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;

namespace LiveCaptions.Desktop;

internal sealed class CaptionStyleWindow : Window
{
    private CaptionAppearance _value;
    private readonly Func<CaptionAppearance, string?> _changed;
    private bool _updating;
    private readonly TextBlock _error = new() { Foreground = new SolidColorBrush(Colors.LightCoral),
        TextWrapping = TextWrapping.Wrap, MaxLines = 2, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    internal SelectorBar Sections { get; } = new();
    internal Page TextPage { get; } = new();
    internal Page BackgroundPage { get; } = new() { Visibility = Visibility.Collapsed };
    private readonly ComboBox _presets = new() { Header = "预设字号", HorizontalAlignment = HorizontalAlignment.Stretch };
    internal ComboBox Fonts { get; } = new() { Header = "字体", HorizontalAlignment = HorizontalAlignment.Stretch };
    internal NumberBox SizeInput { get; } = new() { Header = "字号", Minimum = 12, Maximum = 96,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, SmallChange = 1, LargeChange = 4 };
    internal ComboBox Edges { get; } = new() { Header = "文字效果", HorizontalAlignment = HorizontalAlignment.Stretch };
    internal Slider OpacityInput { get; } = new() { Header = "背景不透明度", Minimum = 0, Maximum = 100, StepFrequency = 1 };
    private readonly TextBox _textHex = new() { Header = "文字颜色", MaxLength = 7 };
    private readonly TextBox _backgroundHex = new() { Header = "背景颜色", MaxLength = 7 };
    private readonly ColorPicker _textPicker = new() { IsAlphaEnabled = false, IsAlphaSliderVisible = false, IsAlphaTextInputVisible = false };
    private readonly ColorPicker _backgroundPicker = new() { IsAlphaEnabled = false, IsAlphaSliderVisible = false, IsAlphaTextInputVisible = false };
    internal ColorPicker TextPicker => _textPicker;
    private Button? _textPaletteButton;
    internal void OpenTextPalette() => _textPaletteButton!.Flyout.ShowAt(_textPaletteButton);

    internal CaptionStyleWindow(CaptionAppearance value, Func<CaptionAppearance, string?> changed)
    {
        _value = value.Normalize(); _changed = changed; Title = "字幕样式";
        var root = new Grid { RequestedTheme = ElementTheme.Dark, Padding = new Thickness(20), RowSpacing = 16,
            Background = (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"] };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        Sections.Items.Add(new SelectorBarItem { Text = "文字" });
        Sections.Items.Add(new SelectorBarItem { Text = "背景" });
        Sections.SelectedItem = Sections.Items[0];
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(Sections, "样式分类");
        root.Children.Add(Sections);
        var textOptions = new StackPanel { Spacing = 16 };
        var backgroundOptions = new StackPanel { Spacing = 20 };
        foreach (var font in CaptionAppearance.Fonts) Fonts.Items.Add(new ComboBoxItem { Content = font.Name, Tag = font.Key });
        foreach (var name in new[] { "无", "凸起", "凹陷", "统一描边", "投影" }) Edges.Items.Add(name);
        textOptions.Children.Add(Fonts);
        var sizeRow = new Grid { ColumnSpacing = 12 };
        sizeRow.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        sizeRow.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        foreach (var (label, size) in new[] { ("小 · 16", 16d), ("中 · 22", 22d), ("大 · 28", 28d), ("特大 · 36", 36d) })
            _presets.Items.Add(new ComboBoxItem { Content = label, Tag = size });
        _presets.Items.Add(new ComboBoxItem { Content = "自定义", Tag = double.NaN });
        _presets.SelectionChanged += (_, _) => { if (!_updating && _presets.SelectedItem is ComboBoxItem item && double.IsFinite((double)item.Tag)) SizeInput.Value = (double)item.Tag; };
        sizeRow.Children.Add(_presets); Grid.SetColumn(SizeInput, 1); sizeRow.Children.Add(SizeInput); textOptions.Children.Add(sizeRow);
        textOptions.Children.Add(Edges);
        textOptions.Children.Add(ColorRow(_textHex, _textPicker, "选择文字颜色", true));
        backgroundOptions.Children.Add(ColorRow(_backgroundHex, _backgroundPicker, "选择背景颜色", false));
        backgroundOptions.Children.Add(OpacityInput);
        TextPage.Content = textOptions;
        BackgroundPage.Content = backgroundOptions;
        var pages = new Grid(); pages.Children.Add(TextPage); pages.Children.Add(BackgroundPage);
        Grid.SetRow(pages, 1); root.Children.Add(pages);
        Sections.SelectionChanged += (_, _) =>
        {
            var text = Sections.SelectedItem == Sections.Items[0];
            TextPage.Visibility = text ? Visibility.Visible : Visibility.Collapsed;
            BackgroundPage.Visibility = text ? Visibility.Collapsed : Visibility.Visible;
        };
        var footer = new Grid { ColumnSpacing = 12, MinHeight = 40 };
        footer.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        footer.Children.Add(_error);
        var reset = new Button { Content = "恢复默认", VerticalAlignment = VerticalAlignment.Center };
        reset.Click += (_, _) => { _value = new(); Sync(); Commit(); };
        Grid.SetColumn(reset, 1); footer.Children.Add(reset);
        Grid.SetRow(footer, 2); root.Children.Add(footer); Content = root;
        var windowStyle = new NativeWindowStyle(this) { MinimumWidth = 480, MinimumHeight = 520 };
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        { presenter.IsResizable = false; presenter.IsMaximizable = false; }
        windowStyle.SetSize(480, 520);
        root.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Escape) { Close(); e.Handled = true; } };
        Fonts.SelectionChanged += (_, _) => { if (!_updating && Fonts.SelectedItem is ComboBoxItem item) Change(_value with { Font = (string)item.Tag }); };
        Edges.SelectionChanged += (_, _) => { if (!_updating && Edges.SelectedIndex >= 0) Change(_value with { Edge = (CaptionEdge)Edges.SelectedIndex }); };
        SizeInput.ValueChanged += (_, e) => { if (!_updating && double.IsFinite(e.NewValue)) Change(_value with { FontSize = e.NewValue }); };
        OpacityInput.ValueChanged += (_, e) => { if (!_updating) Change(_value with { BackgroundOpacity = e.NewValue / 100 }); };
        Sync();
    }

    private Grid ColorRow(TextBox hex, ColorPicker picker, string label, bool text)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.Children.Add(hex);
        var button = new Button { Content = "调色板", VerticalAlignment = VerticalAlignment.Bottom };
        if (text) _textPaletteButton = button;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label);
        var panel = new StackPanel { Spacing = 12 };
        var swatches = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var color in new[] { "#FFFFFF", "#000000", "#252525", "#FFFF00", "#00FFFF", "#FF8080" })
        {
            var swatch = new Button { Width = 32, Height = 32, Background = new SolidColorBrush(CaptionAppearance.ParseColor(color)),
                BorderThickness = new Thickness(1) };
            ToolTipService.SetToolTip(swatch, color);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(swatch, color);
            swatch.Click += (_, _) => picker.Color = CaptionAppearance.ParseColor(color);
            swatches.Children.Add(swatch);
        }
        panel.Children.Add(swatches); panel.Children.Add(picker);
        button.Flyout = new Flyout { Content = panel };
        Grid.SetColumn(button, 1); row.Children.Add(button);
        void ApplyHex()
        {
            var normalized = CaptionAppearance.NormalizeColor(hex.Text, "");
            if (normalized.Length == 0) { ReportSaveError("颜色请输入 #RRGGBB，例如 #FFFFFF。"); return; }
            Change(text ? _value with { TextColor = normalized } : _value with { BackgroundColor = normalized });
            _updating = true; picker.Color = CaptionAppearance.ParseColor(normalized); _updating = false;
        }
        hex.LostFocus += (_, _) => ApplyHex();
        hex.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) { ApplyHex(); e.Handled = true; } };
        picker.ColorChanged += (_, e) =>
        {
            if (_updating) return;
            var color = $"#{e.NewColor.R:X2}{e.NewColor.G:X2}{e.NewColor.B:X2}";
            hex.Text = color;
            Change(text ? _value with { TextColor = color } : _value with { BackgroundColor = color });
        };
        return row;
    }
    private void Sync()
    {
        _updating = true;
        Fonts.SelectedIndex = Array.FindIndex(CaptionAppearance.Fonts, x => x.Key == _value.Font);
        SizeInput.Value = _value.FontSize; Edges.SelectedIndex = (int)_value.Edge;
        OpacityInput.Value = _value.BackgroundOpacity * 100;
        _textHex.Text = _value.TextColor; _backgroundHex.Text = _value.BackgroundColor;
        _textPicker.Color = CaptionAppearance.ParseColor(_value.TextColor);
        _backgroundPicker.Color = CaptionAppearance.ParseColor(_value.BackgroundColor);
        _updating = false; SyncPreset();
    }
    private void Change(CaptionAppearance appearance) { _value = appearance.Normalize(); Commit(); }
    private void Commit() { SyncPreset(); ReportSaveError(_changed(_value) ?? ""); }
    private void SyncPreset()
    {
        var wasUpdating = _updating; _updating = true;
        _presets.SelectedItem = _presets.Items.Cast<ComboBoxItem>().FirstOrDefault(x => (double)x.Tag == _value.FontSize) ?? _presets.Items.Last();
        _updating = wasUpdating;
    }
    internal void ReportSaveError(string message) => _error.Text = message;
}
