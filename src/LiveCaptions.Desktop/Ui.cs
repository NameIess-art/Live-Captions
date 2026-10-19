using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace LiveCaptions.Desktop;

internal static class Ui
{
    internal static Button IconButton(string glyph, string label)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 16 },
            Width = 40, Height = 40, Padding = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0)
        };
        AutomationProperties.SetName(button, label);
        ToolTipService.SetToolTip(button, label);
        return button;
    }
    internal static ContentDialog CreateDialog(Window window, string title, string text, string? primary = null) =>
        new()
        {
            XamlRoot = ((FrameworkElement)window.Content).XamlRoot, Title = title, Content = text,
            CloseButtonText = primary == null ? "确定" : "取消",
            PrimaryButtonText = primary ?? "", DefaultButton = ContentDialogButton.Close,
            RequestedTheme = ElementTheme.Dark
        };
    internal static async Task<ContentDialogResult> Dialog(Window window, string title, string text,
        string? primary = null) => await CreateDialog(window, title, text, primary).ShowAsync();
    internal static ScrollViewer Scroll(UIElement content) => new()
    {
        Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Disabled
    };
}
