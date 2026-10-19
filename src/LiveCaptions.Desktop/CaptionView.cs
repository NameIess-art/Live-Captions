using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace LiveCaptions.Desktop;

// One selectable RichTextBlock plus noninteractive text layers for edge effects.
internal sealed partial class CaptionView : Grid
{
    private readonly Grid _layers = new();
    private readonly List<(RichTextBlock Text, Run Run)> _texts = [];
    internal RichTextBlock Text { get; }
    internal ScrollViewer Scroll { get; }
    internal string Value { get; private set; } = "";
    internal double LineHeight { get; private set; } = 30;
    private bool _following;
    private bool _tailPending;
    internal int TextUpdates { get; private set; }
    private CaptionAppearance? _applied;

    internal CaptionView()
    {
        Scroll = new ScrollViewer
        {
            Content = _layers, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Disabled, VerticalScrollMode = ScrollMode.Disabled,
            ZoomMode = ZoomMode.Disabled, IsTabStop = false
        };
        // Eight outline layers also cover raised, depressed and shadow variants.
        for (var i = 0; i < 8; i++) AddText(false);
        Text = AddText(true);
        ConfigureSelection();
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(Text, "实时字幕");
        Children.Add(Scroll);
        SizeChanged += (_, _) => Quantize();
        _layers.SizeChanged += (_, _) => RequestTail();
        Apply(new());
    }
    private RichTextBlock AddText(bool selectable)
    {
        var run = new Run();
        var paragraph = new Paragraph();
        paragraph.Inlines.Add(run);
        var text = new RichTextBlock
        {
            IsTextSelectionEnabled = selectable, IsHitTestVisible = selectable,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 2, 2, 2),
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight
        };
        text.Blocks.Add(paragraph);
        _texts.Add((text, run)); _layers.Children.Add(text);
        return text;
    }
    internal void SetText(string value)
    {
        value = value.Replace("\r\n", "\n").Replace('\r', '\n');
        if (Value == value) return;
        var selected = Text.SelectedText;
        var start = Math.Max(0, (Text.SelectionStart?.Offset ?? 0) - _texts[^1].Run.ContentStart.Offset);
        var position = selected.Length > 0 && start + selected.Length <= Value.Length &&
            Value.AsSpan(start, selected.Length).SequenceEqual(selected.AsSpan()) ? start : -1;
        if (_selecting) RemapSelection(Value, value);
        // Run replacement can raise SelectionChanged with stale native offsets.
        // Restore our remapped range only after all visible layers are updated.
        _applyingSelection = true;
        try
        {
            Value = value;
            TextUpdates++;
            foreach (var (text, run) in _texts)
                if (text.Visibility == Visibility.Visible) run.Text = Value;
            if (_selecting || selected.Length > 0) UpdateLayout();
            if (!_selecting && selected.Length > 0)
            {
                if (position < 0 || position + selected.Length > Value.Length ||
                    !Value.AsSpan(position, selected.Length).SequenceEqual(selected.AsSpan()))
                    position = Value.IndexOf(selected, StringComparison.Ordinal);
                if (position >= 0)
                {
                    var from = _texts[^1].Run.ContentStart.GetPositionAtOffset(position, LogicalDirection.Forward);
                    var to = from?.GetPositionAtOffset(selected.Length, LogicalDirection.Forward);
                    if (from != null && to != null) Text.Select(from, to);
                }
            }
        }
        finally { _applyingSelection = false; }
        if (_selecting) ApplySelectionRange();
        RequestTail();
    }
    internal void Apply(CaptionAppearance appearance)
    {
        appearance = appearance.Normalize();
        if (_applied is { } previous && previous.Font == appearance.Font && previous.FontSize == appearance.FontSize &&
            previous.Edge == appearance.Edge && previous.TextColor == appearance.TextColor) return;
        _applied = appearance;
        LineHeight = Math.Ceiling(appearance.FontSize * 1.5);
        var family = new FontFamily(CaptionAppearance.Fonts.First(x => x.Key == appearance.Font).Family);
        var offsets = new (double X, double Y)[] { (-1, -1), (0, -1), (1, -1), (-1, 0), (1, 0), (-1, 1), (0, 1), (1, 1) };
        for (var i = 0; i < _texts.Count; i++)
        {
            var text = _texts[i].Text;
            text.FontFamily = family; text.FontSize = appearance.FontSize; text.LineHeight = LineHeight;
            text.Opacity = 1;
            text.Foreground = new SolidColorBrush(i == 8 ? CaptionAppearance.ParseColor(appearance.TextColor) : Colors.Black);
            if (i == 8) continue;
            text.Visibility = Visibility.Collapsed;
            if (appearance.Edge == CaptionEdge.None) continue;
            if (appearance.Edge == CaptionEdge.Uniform)
            {
                text.Visibility = Visibility.Visible;
                text.RenderTransform = new TranslateTransform { X = offsets[i].X, Y = offsets[i].Y };
            }
            else if (i < 2)
            {
                text.Visibility = Visibility.Visible;
                var sign = appearance.Edge == CaptionEdge.Depressed ? -1 : 1;
                text.RenderTransform = new TranslateTransform { X = sign * (i == 0 ? 1 : -1), Y = sign * (i == 0 ? 1 : -1) };
                text.Foreground = new SolidColorBrush(i == 1 && appearance.Edge != CaptionEdge.Shadow ? Colors.White : Colors.Black);
                text.Opacity = appearance.Edge == CaptionEdge.Shadow ? 0.6 : 0.85;
            }
        }
        foreach (var (text, run) in _texts) run.Text = text.Visibility == Visibility.Visible ? Value : "";
        Quantize(); RequestTail();
    }
    private void Quantize()
    {
        // The text's margins belong to the scroll extent. Adding them to the
        // viewport exposed descenders from the preceding, clipped line.
        var height = Math.Max(0, Math.Floor(Math.Max(0, ActualHeight) / LineHeight) * LineHeight);
        if (Scroll.Height != height) Scroll.Height = height;
        Scroll.VerticalAlignment = VerticalAlignment.Bottom;
        RequestTail();
    }
    private void RequestTail()
    {
        if (_tailPending) return;
        _tailPending = true;
        LayoutUpdated += OnTailLayout;
    }
    private void OnTailLayout(object? sender, object e)
    {
        LayoutUpdated -= OnTailLayout;
        _tailPending = false;
        FollowTail();
    }
    internal void FollowTail()
    {
        if (_following || Math.Abs(Scroll.VerticalOffset - Scroll.ScrollableHeight) < 0.1) return;
        _following = true;
        try { Scroll.ChangeView(null, Scroll.ScrollableHeight, null, true); }
        finally { _following = false; }
    }
    internal bool HitText(Point rootPoint, UIElement root)
    {
        if (Value.Length == 0) return false;
        // Selection also starts in line spacing or to the right of a line. Keep
        // the whole visible text control interactive, not just individual glyphs.
        var textBounds = Text.TransformToVisual(root).TransformBounds(new Rect(0, 0, Text.ActualWidth, Text.ActualHeight));
        var viewport = Scroll.TransformToVisual(root).TransformBounds(new Rect(0, 0, Scroll.ActualWidth, Scroll.ActualHeight));
        return textBounds.Contains(rootPoint) && viewport.Contains(rootPoint);
    }
}
