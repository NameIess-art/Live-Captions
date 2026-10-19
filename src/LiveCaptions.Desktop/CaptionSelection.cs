using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace LiveCaptions.Desktop;

internal sealed partial class CaptionView
{
    private Pointer? _selectionPointer;
    private bool _selecting, _applyingSelection;
    private int _selectionAnchor, _selectionExtent;
    private Point _selectionViewportPoint;

    private void ConfigureSelection()
    {
        Text.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            var point = e.GetCurrentPoint(Text);
            if (!point.Properties.IsLeftButtonPressed || _selectionPointer != null) return;
            if (!BeginSelection(point.Position)) return;
            _selectionPointer = e.Pointer;
            if (!Text.CapturePointer(e.Pointer)) EndSelection();
            e.Handled = true;
        }), true);
        Text.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler((_, e) =>
        {
            if (_selectionPointer?.PointerId != e.Pointer.PointerId) return;
            var point = e.GetCurrentPoint(Text);
            if (!point.Properties.IsLeftButtonPressed) EndSelection();
            else ExtendSelection(point.Position);
            e.Handled = true;
        }), true);
        Text.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, e) =>
        {
            if (_selectionPointer?.PointerId != e.Pointer.PointerId) return;
            var point = e.GetCurrentPoint(Text);
            if (point.Properties.IsLeftButtonPressed) return;
            ExtendSelection(point.Position);
            EndSelection();
            e.Handled = true;
        }), true);
        PointerEventHandler cancel = (_, e) =>
        {
            if (_selectionPointer?.PointerId == e.Pointer.PointerId) EndSelection();
        };
        Text.AddHandler(UIElement.PointerCanceledEvent, cancel, true);
        Text.AddHandler(UIElement.PointerCaptureLostEvent, cancel, true);
        Text.SelectionChanged += (_, _) =>
        {
            // Native word snapping and an earlier native drag anchor must not
            // expand our character range before/after the current pointer anchor.
            if (_selecting && !_applyingSelection) ApplySelectionRange();
        };
        Unloaded += (_, _) => EndSelection();
    }

    internal bool BeginSelection(Point point)
    {
        EndSelection();
        var caret = Text.GetPositionFromPoint(point);
        if (caret == null) return false;
        _selectionAnchor = _selectionExtent = TextOffset(caret);
        _selectionViewportPoint = Text.TransformToVisual(Scroll).TransformPoint(point);
        _selecting = true;
        ApplySelectionRange();
        return true;
    }

    internal void ExtendSelection(Point point)
    {
        if (!_selecting) return;
        // A live update can scroll the text under a captured, stationary pointer.
        // Only actual pointer movement extends the range; scrolling never does.
        var viewport = Text.TransformToVisual(Scroll).TransformPoint(point);
        if (Math.Abs(viewport.X - _selectionViewportPoint.X) < .1 && Math.Abs(viewport.Y - _selectionViewportPoint.Y) < .1) return;
        _selectionViewportPoint = viewport;
        var caret = Text.GetPositionFromPoint(point);
        if (caret == null) return;
        _selectionExtent = TextOffset(caret);
        ApplySelectionRange();
    }

    private int TextOffset(TextPointer pointer) => Math.Clamp(pointer.Offset - _texts[^1].Run.ContentStart.Offset, 0, Value.Length);

    private void ApplySelectionRange()
    {
        _selectionAnchor = Math.Clamp(_selectionAnchor, 0, Value.Length);
        _selectionExtent = Math.Clamp(_selectionExtent, 0, Value.Length);
        var start = _texts[^1].Run.ContentStart.GetPositionAtOffset(Math.Min(_selectionAnchor, _selectionExtent), LogicalDirection.Forward);
        var end = _texts[^1].Run.ContentStart.GetPositionAtOffset(Math.Max(_selectionAnchor, _selectionExtent), LogicalDirection.Forward);
        if (start == null || end == null || Text.SelectionStart.Offset == start.Offset && Text.SelectionEnd.Offset == end.Offset) return;
        _applyingSelection = true;
        try { Text.Select(start, end); }
        finally { _applyingSelection = false; }
    }

    private void RemapSelection(string previous, string next)
    {
        var prefix = 0;
        while (prefix < Math.Min(previous.Length, next.Length) && previous[prefix] == next[prefix]) prefix++;
        var suffix = 0;
        while (suffix < Math.Min(previous.Length, next.Length) - prefix && previous[^(suffix + 1)] == next[^(suffix + 1)]) suffix++;
        var removed = 0;
        if (prefix < Math.Min(previous.Length, next.Length))
        {
            // Native captions also discard old lines while appending new ones.
            // Match the retained suffix in linear time, rather than guessing by
            // the selected phrase (which can occur more than once).
            var combined = next + '\0' + previous;
            var lengths = new int[combined.Length];
            for (var i = 1; i < combined.Length; i++)
            {
                var j = lengths[i - 1];
                while (j > 0 && combined[i] != combined[j]) j = lengths[j - 1];
                if (combined[i] == combined[j]) j++;
                lengths[i] = j;
            }
            var overlap = Math.Min(lengths[^1], next.Length);
            if (overlap >= Math.Min(4, Math.Min(previous.Length, next.Length)) && overlap > prefix + suffix)
                removed = previous.Length - overlap;
        }
        if (prefix == 0 && suffix == 0 && removed == 0 && previous.Length > 0)
        {
            // A leading recognition correction and a trailing append can leave
            // neither edge unchanged while the selected words remain intact.
            var start = Math.Min(_selectionAnchor, _selectionExtent);
            var end = Math.Max(_selectionAnchor, _selectionExtent);
            if (start == end) { start = Math.Max(0, start - 16); end = Math.Min(previous.Length, end + 16); }
            var retained = FindRetainedRange(previous, next, start, end);
            if (retained >= 0)
            {
                _selectionAnchor += retained - start;
                _selectionExtent += retained - start;
                return;
            }
            // The selected utterance disappeared completely. Do not highlight
            // unrelated words at the old offsets in the replacement caption.
            _selectionAnchor = _selectionExtent = Math.Min(_selectionAnchor, next.Length);
            return;
        }
        int Map(int offset)
        {
            if (removed > 0) return Math.Clamp(offset - removed, 0, next.Length);
            if (offset < prefix || prefix == previous.Length) return offset;
            if (offset >= previous.Length - suffix) return Math.Clamp(offset + next.Length - previous.Length, 0, next.Length);
            return prefix + Math.Min(offset - prefix, next.Length - prefix - suffix);
        }
        _selectionAnchor = Map(_selectionAnchor);
        _selectionExtent = Map(_selectionExtent);
    }

    private static int FindRetainedRange(string previous, string next, int start, int end)
    {
        if (end <= start) return -1;
        var phrase = previous[start..end];
        var best = -1;
        var bestContext = -1;
        for (var at = next.IndexOf(phrase, StringComparison.Ordinal); at >= 0;
            at = next.IndexOf(phrase, at + 1, StringComparison.Ordinal))
        {
            var context = 0;
            for (var i = 1; i <= 32 && i <= start && i <= at && previous[start - i] == next[at - i]; i++) context++;
            for (var i = 0; i < 32 && end + i < previous.Length && at + phrase.Length + i < next.Length &&
                previous[end + i] == next[at + phrase.Length + i]; i++) context++;
            if (context > bestContext || context == bestContext && Math.Abs(at - start) < Math.Abs(best - start))
            { best = at; bestContext = context; }
        }
        return best;
    }

    internal void EndSelection()
    {
        _selecting = false;
        var pointer = _selectionPointer;
        _selectionPointer = null;
        if (pointer != null) Text.ReleasePointerCapture(pointer);
        RequestTail();
    }
}
