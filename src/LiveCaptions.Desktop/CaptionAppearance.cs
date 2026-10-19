using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace LiveCaptions.Desktop;

internal enum CaptionEdge { None, Raised, Depressed, Uniform, Shadow }
internal sealed record CaptionFont(string Key, string Name, string Family);
internal sealed record CaptionAppearance
{
    public string TextColor { get; init; } = "#FFFFFF";
    public string Font { get; init; } = "sans";
    public double FontSize { get; init; } = 22;
    public CaptionEdge Edge { get; init; }
    public string BackgroundColor { get; init; } = "#252525";
    public double BackgroundOpacity { get; init; } = 1;
    internal static readonly CaptionFont[] Fonts =
    [
        new("mono-serif", "单衬线", "Courier New, SimSun"),
        new("serif", "非常宽有衬线", "Georgia, SimSun"),
        new("mono-sans", "等宽无衬线", "Consolas, Microsoft YaHei UI"),
        new("sans", "非常宽无衬线", "Microsoft YaHei UI, Segoe UI"),
        new("casual", "随意", "Comic Sans MS, Microsoft YaHei UI"),
        new("cursive", "草书", "Segoe Script, KaiTi")
    ];
    internal CaptionAppearance Normalize() => this with
    {
        TextColor = NormalizeColor(TextColor, "#FFFFFF"),
        Font = Fonts.Any(x => x.Key == Font) ? Font : "sans",
        FontSize = double.IsFinite(FontSize) ? Math.Clamp(FontSize, 12, 96) : 22,
        Edge = Enum.IsDefined(Edge) ? Edge : CaptionEdge.None,
        BackgroundColor = NormalizeColor(BackgroundColor, "#252525"),
        BackgroundOpacity = double.IsFinite(BackgroundOpacity) ? Math.Clamp(BackgroundOpacity, 0, 1) : 1
    };
    internal static string NormalizeColor(string? value, string fallback) =>
        value?.Length == 7 && value[0] == '#' && uint.TryParse(value.AsSpan(1), NumberStyles.HexNumber, null, out _)
            ? value.ToUpperInvariant() : fallback;
    internal static Color ParseColor(string value) => Color.FromArgb(255,
        byte.Parse(value.AsSpan(1, 2), NumberStyles.HexNumber),
        byte.Parse(value.AsSpan(3, 2), NumberStyles.HexNumber),
        byte.Parse(value.AsSpan(5, 2), NumberStyles.HexNumber));
    internal SolidColorBrush BackgroundBrush()
    {
        var color = ParseColor(BackgroundColor);
        color.A = (byte)Math.Round(BackgroundOpacity * 255);
        return new(color);
    }
}
