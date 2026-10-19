using Microsoft.UI.Xaml.Documents;
using Windows.Foundation;
using Windows.Graphics;

namespace LiveCaptions.Desktop;

public sealed partial class MainWindow
{
    private void VerifyBlankPress(Action<bool, string> check, Point blank, Point glyph)
    {
        var origin = AppWindow.Position;
        var screen = new PointInt32(origin.X + (int)Math.Round(blank.X * _windowStyle.Scale),
            origin.Y + (int)Math.Round(blank.Y * _windowStyle.Scale));
        var start = ((Run)((Paragraph)_caption.Text.Blocks[0]).Inlines[0]).ContentStart;
        void SelectText() => _caption.Text.Select(start, start.GetPositionAtOffset(3, LogicalDirection.Forward));
        try
        {
            SelectText();
            check(_caption.Text.SelectedText.Length > 0, "Blank click test starts with a real live selection");
            check(!StartBlankPress(_caption.Text, glyph, screen) && _caption.Text.SelectedText.Length > 0,
                "Text interaction keeps selection and never arms window dragging");
            check(StartBlankPress(_surface, blank, screen) && _caption.Text.SelectedText.Length == 0 && AppWindow.Position == origin,
                "Blank press immediately clears selection and keeps the window still until pointer movement");
            CancelBlankPress(); // Pointer release/cancel/capture loss share this path.
            MoveBlankPress(new(screen.X + 30, screen.Y + 15));
            check(!_blankPressActive && AppWindow.Position == origin,
                "Released blank click cannot move the window");

            StartBlankPress(_surface, blank, screen);
            MoveBlankPress(new(screen.X + 2, screen.Y));
            check(AppWindow.Position.X == origin.X + 2 && AppWindow.Position.Y == origin.Y,
                "The first small pointer movement immediately drags the window without waiting");
            CancelBlankPress(); AppWindow.Move(origin);

            StartBlankPress(_surface, blank, screen);
            var dx = 20;
            var dy = 12;
            var moved = new PointInt32(screen.X + dx, screen.Y + dy);
            MoveBlankPress(moved);
            check(AppWindow.Position.X == origin.X + dx && AppWindow.Position.Y == origin.Y + dy,
                "Immediate movement uses physical pixels without rescaling the pointer delta");
            MoveBlankPress(moved);
            check(AppWindow.Position.X == origin.X + dx && AppWindow.Position.Y == origin.Y + dy,
                "Repeated screen samples cannot feed window movement back into the next delta");
            var stable = true;
            for (var i = 0; i < 160; i++)
            {
                var x = (i % 2 == 0 ? 1 : -1) * (10 + i % 17 * 7);
                var y = (i % 3 - 1) * 35;
                var sample = new PointInt32(screen.X + x, screen.Y + y);
                MoveBlankPress(sample);
                MoveBlankPress(sample); // queued events may see the same latest cursor
                stable &= AppWindow.Position.X == origin.X + x && AppWindow.Position.Y == origin.Y + y;
            }
            check(stable, "160 rapid direction changes and duplicate samples keep every window position anchored to the screen pointer");
            // A delayed window notification/position must not contaminate the
            // next pointer sample, which still belongs to the same screen point.
            AppWindow.Move(new(origin.X + 90, origin.Y - 45));
            MoveBlankPress(moved);
            check(AppWindow.Position.X == origin.X + dx && AppWindow.Position.Y == origin.Y + dy,
                "An intervening window position cannot change the target for the same pointer sample");
            CancelBlankPress();
            var released = AppWindow.Position;
            MoveBlankPress(new(screen.X + 40, screen.Y + 25));
            check(AppWindow.Position == released && !_blankPressActive, "Releasing the pointer ends window movement");
            AppWindow.Move(origin);
            StartBlankPress(_surface, blank, new(-1400, -300));
            MoveBlankPress(new(-1393, -309));
            check(AppWindow.Position.X == origin.X + 7 && AppWindow.Position.Y == origin.Y - 9,
                "Negative virtual-screen coordinates preserve signed pixel deltas");
        }
        finally { CancelBlankPress(); AppWindow.Move(origin); }
    }
}
