using Microsoft.UI.Xaml.Documents;
using Windows.Foundation;

namespace LiveCaptions.Desktop;

public sealed partial class MainWindow
{
    private async Task VerifyCaptionSelectionAsync(Action<bool, string> check)
    {
        var value = _caption.Value;
        var run = (Run)((Paragraph)_caption.Text.Blocks[0]).Inlines[0];
        TextPointer At(int offset) => run.ContentStart.GetPositionAtOffset(offset, LogicalDirection.Forward);
        Point PointAt(int offset)
        {
            var rect = At(offset).GetCharacterRect(LogicalDirection.Forward);
            return new(rect.X + .1, rect.Y + rect.Height / 2);
        }
        try
        {
            _caption.Text.Select(At(2), At(5));
            check(_caption.BeginSelection(PointAt(3)) && _caption.Text.SelectedText.Length == 0 &&
                _caption.Text.SelectionStart.Offset == At(3).Offset,
                "Starting inside an old selection clears it and anchors at the exact press");
            _caption.ExtendSelection(PointAt(5));
            check(_caption.Text.SelectedText == value.Substring(3, 2), "Forward drag excludes every character before the press anchor");
            // Simulate the native text control expanding to an old/word anchor.
            _caption.Text.Select(At(0), At(5));
            await Task.Delay(60); // WinUI delivers SelectionChanged asynchronously.
            check(_caption.Text.SelectedText == value.Substring(3, 2), "Native word snapping cannot add text before the fixed anchor");
            _caption.ExtendSelection(PointAt(1));
            check(_caption.Text.SelectedText == value.Substring(1, 2) && _caption.Text.SelectionEnd.Offset == At(3).Offset,
                "Backward drag retains the original press as its other boundary");
            _caption.ExtendSelection(PointAt(6));
            check(_caption.Text.SelectedText == value.Substring(3, 3), "Crossing the anchor again discards the previous backward extent");
            _caption.EndSelection();

            check(_caption.BeginSelection(PointAt(8)) && _caption.Text.SelectedText.Length == 0,
                "A second drag replaces the prior selection with a new fixed anchor");
            _caption.ExtendSelection(PointAt(11));
            check(_caption.Text.SelectedText == "第二句", "The new drag contains only its own character range");
            var updates = _caption.TextUpdates;
            _caption.SetText("新增" + value);
            var latest = "最新" + value + "追加";
            _caption.SetText(latest);
            await Task.Delay(60);
            check(_caption.Value == latest && run.Text == latest && _caption.TextUpdates == updates + 2 && _caption.Text.SelectedText == "第二句",
                "Every capture is displayed during dragging while preserving the selected characters");
            _caption.ExtendSelection(PointAt(14));
            check(_caption.Text.SelectedText == latest.Substring(10, 4),
                "Dragging after a live update retains the remapped press anchor");
            _caption.EndSelection();
            check(_caption.Value == latest && _caption.TextUpdates == updates + 2 && _caption.Text.SelectedText == latest.Substring(10, 4),
                "Release preserves the live selection without replaying buffered captions");

            _caption.SetText("旧行甲乙丙丁。保留的第二句。末尾"); _caption.UpdateLayout();
            _caption.BeginSelection(PointAt(12)); _caption.ExtendSelection(PointAt(7));
            _caption.SetText("保留的第二句。末尾。新字幕");
            await Task.Delay(60);
            check(_caption.Text.SelectedText == "保留的第二",
                "Rolling capture trims old text without losing a backward selection in the retained text");
            _caption.ExtendSelection(PointAt(8));
            check(_caption.Text.SelectedText == "句。末", "Backward selection can cross its remapped anchor after a rolling update");
            _caption.EndSelection();

            _caption.SetText("重复句。重复句。后续"); _caption.UpdateLayout();
            _caption.BeginSelection(PointAt(4)); _caption.ExtendSelection(PointAt(7));
            _caption.SetText("重复句。重复句。后续追加");
            await Task.Delay(60);
            check(_caption.Text.SelectedText == "重复句" && _caption.Text.SelectionStart.Offset == At(4).Offset,
                "Appending speech keeps the selected occurrence when text repeats");
            _caption.SetText("重复句。重述句。后续追加");
            await Task.Delay(60);
            check(_caption.Text.SelectedText == "重述句", "Recognition corrections update selected text in place during dragging");
            _caption.SetText("全新内容");
            await Task.Delay(60);
            check(_caption.Text.SelectedText.Length == 0 && _caption.Text.SelectionStart.Offset >= At(0).Offset &&
                _caption.Text.SelectionEnd.Offset <= At(_caption.Value.Length).Offset,
                "Replacing the entire utterance clears the old selection instead of highlighting unrelated words");
            _caption.SetText("");
            check(_caption.Text.SelectedText.Length == 0, "An empty live update safely clears a held selection");
            _caption.EndSelection();

            _caption.SetText("原来的文字"); _caption.UpdateLayout();
            _caption.BeginSelection(PointAt(0));
            _caption.SetText("新增原来的文字");
            await Task.Delay(60);
            _caption.ExtendSelection(PointAt(5));
            check(_caption.Text.SelectedText == "原来的", "A held press at the first character tracks inserted text before dragging begins");
            _caption.EndSelection();

            _caption.SetText("保留起点。\n第二行字幕"); _caption.UpdateLayout();
            _caption.BeginSelection(PointAt(0)); _caption.ExtendSelection(PointAt(4));
            for (var i = 0; i < 12; i++)
            {
                _caption.SetText(_caption.Value + $"\n实时追加第{i}行字幕");
                await Task.Delay(16);
            }
            _caption.UpdateLayout(); _caption.FollowTail();
            await Task.Delay(80);
            check(_caption.Scroll.ScrollableHeight > 0 && Math.Abs(_caption.Scroll.VerticalOffset - _caption.Scroll.ScrollableHeight) < 1,
                "Held selection continues to scroll and display the newest caption lines");
            check(_caption.Text.SelectedText == "保留起点", "Live tail following does not move the fixed character anchor");
            var stationary = _caption.Text.TransformToVisual(_caption.Scroll).Inverse.TransformPoint(new Point(10, 10));
            _caption.ExtendSelection(stationary);
            var held = _caption.Text.SelectedText;
            _caption.SetText(_caption.Value + "\n继续实时显示");
            await Task.Delay(80);
            stationary = _caption.Text.TransformToVisual(_caption.Scroll).Inverse.TransformPoint(new Point(10, 10));
            _caption.ExtendSelection(stationary);
            check(_caption.Text.SelectedText == held, "A stationary pointer cannot expand the selection just because new lines scroll");
        }
        finally
        {
            _caption.EndSelection();
            _caption.SetText(value);
            _caption.Text.Select(_caption.Text.ContentStart, _caption.Text.ContentStart);
        }
    }
}
