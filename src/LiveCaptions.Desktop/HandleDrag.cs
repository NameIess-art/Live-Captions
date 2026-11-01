using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;

namespace LiveCaptions.Desktop;

public sealed partial class MainWindow
{
    private Pointer? _dragPointer;
    private bool _dragPressActive;
    private PointInt32 _dragPressScreen, _dragWindowOrigin;

    private void ConfigureHandleDrag()
    {
        AutomationProperties.SetHelpText(_dragHandle, "按住并移动以拖动窗口，松开停止。");
        _dragHandle.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            var point = e.GetCurrentPoint(_root);
            var primary = point.Properties.IsLeftButtonPressed ||
                e.Pointer.PointerDeviceType is PointerDeviceType.Touch or PointerDeviceType.Pen && point.IsInContact;
            if (!primary || _dragPointer != null) return;
            if (!NativeWindowStyle.TryGetPointerScreenPosition(e.Pointer.PointerId, e.Pointer.PointerDeviceType, out var screen)) return;
            if (!StartHandlePress(e.OriginalSource, point.Position, screen)) return;
            // Keep capture on the button itself; transferring ButtonBase's capture
            // to a parent can cause a later capture-lost event to cancel dragging.
            _dragPointer = e.Pointer;
            if (!_dragHandle.CapturePointer(e.Pointer)) CancelHandleDrag();
            e.Handled = true;
        }), true);
        _root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            var point = e.GetCurrentPoint(_root);
            if (point.Properties.IsLeftButtonPressed || point.IsInContact)
                ClearSelectionOnBlankPress(e.OriginalSource, point.Position);
        }), false);
        _root.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler((_, e) =>
        {
            if (_dragPointer?.PointerId != e.Pointer.PointerId) return;
            var point = e.GetCurrentPoint(_root);
            if (!point.IsInContact && !point.Properties.IsLeftButtonPressed) CancelHandleDrag();
            else if (NativeWindowStyle.TryGetPointerScreenPosition(e.Pointer.PointerId, e.Pointer.PointerDeviceType, out var screen))
                MoveHandleDrag(screen);
            else CancelHandleDrag();
            e.Handled = true;
        }), true);
        PointerEventHandler end = (_, e) =>
        {
            if (_dragPointer?.PointerId == e.Pointer.PointerId) CancelHandleDrag();
        };
        _root.AddHandler(UIElement.PointerReleasedEvent, end, true);
        _root.AddHandler(UIElement.PointerCanceledEvent, end, true);
        _root.AddHandler(UIElement.PointerCaptureLostEvent, end, true);
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated)
            { CancelHandleDrag(); _caption.EndSelection(); }
        };
    }

    private bool ClearSelectionOnBlankPress(object source, Point point)
    {
        if (!IsBlankSource(source) || !IsBlankPoint(point)) return false;
        _caption.Text.Select(_caption.Text.ContentStart, _caption.Text.ContentStart);
        return true;
    }

    private bool StartHandlePress(object source, Point point, PointInt32 screen)
    {
        CancelHandleDrag();
        if (_closing || !_windowStyle.IsDragEnabled || !IsDragSource(source) || !IsHandlePoint(point)) return false;
        _dragWindowOrigin = AppWindow.Position;
        _dragPressScreen = screen;
        _dragPressActive = true;
        _dragHandle.Background = new SolidColorBrush(CaptionAppearance.ParseColor("#375A7F"));
        return true;
    }

    private void MoveHandleDrag(PointInt32 screen)
    {
        if (!_dragPressActive) return;
        if (!_windowStyle.IsDragEnabled || _closing) { CancelHandleDrag(); return; }
        var dx = screen.X - _dragPressScreen.X;
        var dy = screen.Y - _dragPressScreen.Y;
        // Use physical screen coordinates so moving the window cannot feed back
        // into the pointer delta, even across monitors or queued duplicate events.
        var target = new PointInt32(_dragWindowOrigin.X + dx, _dragWindowOrigin.Y + dy);
        if (AppWindow.Position != target) AppWindow.Move(target);
    }

    private void CancelHandleDrag()
    {
        _dragPressActive = false;
        _dragHandle.Background = new SolidColorBrush(Colors.Transparent);
        var pointer = _dragPointer;
        _dragPointer = null;
        if (pointer != null) _dragHandle.ReleasePointerCapture(pointer);
    }
}
