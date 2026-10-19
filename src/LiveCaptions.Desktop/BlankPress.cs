using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.Graphics;

namespace LiveCaptions.Desktop;

public sealed partial class MainWindow
{
    private Pointer? _blankPointer;
    private bool _blankPressActive;
    private PointInt32 _blankPressScreen;
    private PointInt32 _blankWindowOrigin;

    private void ConfigureBlankPress()
    {
        _root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            var point = e.GetCurrentPoint(_root);
            var primary = point.Properties.IsLeftButtonPressed ||
                e.Pointer.PointerDeviceType == PointerDeviceType.Touch && point.IsInContact;
            if (e.Handled || !primary || _blankPointer != null) return;
            if (!NativeWindowStyle.TryGetPointerScreenPosition(e.Pointer.PointerId, e.Pointer.PointerDeviceType, out var screen)) return;
            if (!StartBlankPress(e.OriginalSource, point.Position, screen)) return;
            _blankPointer = e.Pointer;
            if (!_root.CapturePointer(e.Pointer)) CancelBlankPress();
            e.Handled = true;
        }), false);
        _root.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler((_, e) =>
        {
            if (_blankPointer?.PointerId != e.Pointer.PointerId) return;
            var point = e.GetCurrentPoint(_root);
            if (!point.IsInContact && !point.Properties.IsLeftButtonPressed) CancelBlankPress();
            else if (NativeWindowStyle.TryGetPointerScreenPosition(e.Pointer.PointerId, e.Pointer.PointerDeviceType, out var screen))
                MoveBlankPress(screen);
            else CancelBlankPress();
        }), true);
        PointerEventHandler end = (_, e) =>
        {
            if (_blankPointer?.PointerId == e.Pointer.PointerId) CancelBlankPress();
        };
        _root.AddHandler(UIElement.PointerReleasedEvent, end, true);
        _root.AddHandler(UIElement.PointerCanceledEvent, end, true);
        _root.AddHandler(UIElement.PointerCaptureLostEvent, end, true);
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated)
            { CancelBlankPress(); _caption.EndSelection(); }
        };
    }

    private bool StartBlankPress(object source, Point point, PointInt32 screen)
    {
        CancelBlankPress();
        if (_closing || !_windowStyle.IsDragEnabled || !IsDragSource(source) || !IsDraggable(point)) return false;
        _caption.Text.Select(_caption.Text.ContentStart, _caption.Text.ContentStart);
        _blankWindowOrigin = AppWindow.Position;
        _blankPressScreen = screen;
        _blankPressActive = true;
        return true;
    }

    private void MoveBlankPress(PointInt32 screen)
    {
        if (!_blankPressActive) return;
        if (!_windowStyle.IsDragEnabled || _closing) { CancelBlankPress(); return; }
        var dx = screen.X - _blankPressScreen.X;
        var dy = screen.Y - _blankPressScreen.Y;
        // Never combine an old event's client coordinates with a newer window
        // position. Physical screen coordinates do not change when we move.
        var target = new PointInt32(_blankWindowOrigin.X + dx, _blankWindowOrigin.Y + dy);
        if (AppWindow.Position != target) AppWindow.Move(target);
    }

    private void CancelBlankPress()
    {
        _blankPressActive = false;
        var pointer = _blankPointer;
        _blankPointer = null;
        if (pointer != null) _root.ReleasePointerCapture(pointer);
    }
}
