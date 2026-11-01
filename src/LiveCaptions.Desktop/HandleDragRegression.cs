using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Documents;
using Windows.Foundation;
using Windows.Graphics;

namespace LiveCaptions.Desktop;

public sealed partial class MainWindow
{
    private async Task VerifyHandleDragAsync(Action<bool, string> check, Point blank, Point glyph, Point handle)
    {
        var origin = AppWindow.Position;
        var screen = new PointInt32(origin.X + (int)Math.Round(handle.X * _windowStyle.Scale),
            origin.Y + (int)Math.Round(handle.Y * _windowStyle.Scale));
        var start = ((Run)((Paragraph)_caption.Text.Blocks[0]).Inlines[0]).ContentStart;
        try
        {
            _caption.Text.Select(start, start.GetPositionAtOffset(3, LogicalDirection.Forward));
            check(!StartHandlePress(_caption.Text, glyph, screen) && _caption.Text.SelectedText.Length > 0,
                "Text interaction keeps selection and never arms window dragging");
            check(!StartHandlePress(_surface, blank, screen), "Blank areas cannot arm dragging");
            check(ClearSelectionOnBlankPress(_surface, blank) && _caption.Text.SelectedText.Length == 0,
                "Blank click still clears the live text selection");
            check(!StartHandlePress(_dragHandle, blank, screen), "Handle source cannot start a drag outside the button bounds");
            check(StartHandlePress(_dragHandle, handle, screen) && _dragPressActive && AppWindow.Position == origin,
                "Bottom-right handle activates immediately without jumping the window");
            MoveHandleDrag(new(screen.X + 2, screen.Y));
            check(AppWindow.Position.X == origin.X + 2 && AppWindow.Position.Y == origin.Y,
                "First two-pixel movement drags immediately without a hold delay");
            CancelHandleDrag();
            var afterRelease = AppWindow.Position;
            MoveHandleDrag(new(screen.X + 30, screen.Y));
            check(!_dragPressActive && AppWindow.Position == afterRelease, "Release prevents subsequent pointer movement from dragging");
            AppWindow.Move(origin);
            StartHandlePress(_dragHandle, handle, screen);
            MoveHandleDrag(new(screen.X + 40, screen.Y));
            check(_dragPressActive && AppWindow.Position.X == origin.X + 40 && AppWindow.Position.Y == origin.Y,
                "Immediate larger pointer movement remains an active drag");
            CancelHandleDrag(); AppWindow.Move(origin);
            StartHandlePress(_dragHandle, handle, screen);
            var anchor = screen;
            var moved = new PointInt32(anchor.X + 20, anchor.Y + 12);
            MoveHandleDrag(moved); MoveHandleDrag(moved);
            check(AppWindow.Position.X == origin.X + 20 && AppWindow.Position.Y == origin.Y + 12,
                "Armed handle uses physical screen deltas and ignores duplicate samples");
            var stable = true;
            for (var i = 0; i < 160; i++)
            {
                var x = (i % 2 == 0 ? 1 : -1) * (10 + i % 17 * 7);
                var y = (i % 3 - 1) * 35;
                var sample = new PointInt32(anchor.X + x, anchor.Y + y);
                MoveHandleDrag(sample); MoveHandleDrag(sample);
                stable &= AppWindow.Position.X == origin.X + x && AppWindow.Position.Y == origin.Y + y;
            }
            check(stable, "160 rapid direction changes keep handle dragging anchored to the screen pointer");
            AppWindow.Move(new(origin.X + 90, origin.Y - 45)); MoveHandleDrag(moved);
            check(AppWindow.Position.X == origin.X + 20 && AppWindow.Position.Y == origin.Y + 12,
                "Intervening window movement cannot feed back into the pointer delta");
            CancelHandleDrag(); var released = AppWindow.Position;
            MoveHandleDrag(new(screen.X + 60, screen.Y + 25));
            check(AppWindow.Position == released && !_dragPressActive, "Release or capture loss immediately ends handle dragging");
            AppWindow.Move(origin);
            StartHandlePress(_dragHandle, handle, new(-1400, -300));
            MoveHandleDrag(new(-1393, -309));
            check(AppWindow.Position.X == origin.X + 7 && AppWindow.Position.Y == origin.Y - 9,
                "Handle dragging preserves signed coordinates on negative-position monitors");
        }
        finally { CancelHandleDrag(); AppWindow.Move(origin); }
        await VerifyHandlePointerAsync(check, handle, blank);
    }

    private async Task VerifyHandlePointerAsync(Action<bool, string> check, Point handle, Point blank)
    {
        if ((GetAsyncKeyState(1) & 0x8000) != 0) throw new InvalidOperationException("Pointer test requires the left mouse button to be released.");
        GetCursorPos(out var savedCursor);
        var origin = AppWindow.Position;
        var down = false;
        DragTestPoint Screen(Point point) => new(origin.X + (int)Math.Round(point.X * _windowStyle.Scale),
            origin.Y + (int)Math.Round(point.Y * _windowStyle.Scale));
        async Task Press(Point point)
        {
            var screen = Screen(point);
            if (!SetCursorPos(screen.X, screen.Y)) throw new InvalidOperationException("Pointer regression requires desktop cursor access.");
            mouse_event(1, 0, 0, 0, 0); await Task.Delay(40);
            GetWindowThreadProcessId(WindowFromPoint(screen), out var owner);
            if (owner != Environment.ProcessId) throw new InvalidOperationException("Pointer test may only target this test application's window.");
            mouse_event(2, 0, 0, 0, 0); down = true;
        }
        async Task Release()
        { mouse_event(4, 0, 0, 0, 0); down = false; await Task.Delay(50); }
        try
        {
            await Press(handle); await Task.Delay(40); await Release();
            check(!_dragPressActive && AppWindow.Position == origin, "Real pointer click without motion keeps the window still");
            await Press(handle); await Task.Delay(40);
            check(_dragPressActive && _dragPointer != null, "Real pointer activates handle dragging immediately and retains capture");
            var screen = Screen(handle);
            // Inject motion as well as positioning the cursor so WinUI receives PointerMoved.
            mouse_event(1, 25, unchecked((uint)-14), 0, 0); await Task.Delay(80);
            GetCursorPos(out var movedCursor);
            check(AppWindow.Position.X == origin.X + movedCursor.X - screen.X && AppWindow.Position.Y == origin.Y + movedCursor.Y - screen.Y && AppWindow.Position != origin,
                $"Real captured pointer drags without waiting (origin={origin.X},{origin.Y} actual={AppWindow.Position.X},{AppWindow.Position.Y})");
            await Release(); var released = AppWindow.Position;
            mouse_event(1, 30, unchecked((uint)-6), 0, 0); await Task.Delay(50);
            check(!_dragPressActive && AppWindow.Position == released, "Real pointer release stops dragging immediately");
            AppWindow.Move(origin); await Press(blank); await Task.Delay(460);
            screen = Screen(blank); mouse_event(1, unchecked((uint)-20), 0, 0, 0); await Task.Delay(50);
            check(!_dragPressActive && AppWindow.Position == origin, "Real long press on blank space never moves the window");
            await Release();
        }
        finally
        {
            if (down) mouse_event(4, 0, 0, 0, 0);
            CancelHandleDrag(); AppWindow.Move(origin); SetCursorPos(savedCursor.X, savedCursor.Y);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private readonly record struct DragTestPoint(int X, int Y);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out DragTestPoint point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, nuint extraInfo);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(DragTestPoint point);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
}
