using System.Text;
using System.Text.RegularExpressions;

namespace LiveCaptions.Core;

public sealed record TranscriptEntry(Guid Id, DateTimeOffset CapturedAt, string Text, bool IsNotice = false);
public sealed record SessionRecord(Guid Id, DateTimeOffset StartedAt, IReadOnlyList<TranscriptEntry> Entries);

/// <summary>Consumes a moving caption window. The latest entry remains revisable.</summary>
public sealed class Transcript
{
    private readonly List<TranscriptEntry> _entries = [];
    private string _previous = "";
    private Guid? _activeId;
    private int _discardedPrefixLength;
    public Guid Id { get; private set; } = Guid.NewGuid();
    public DateTimeOffset StartedAt { get; private set; } = DateTimeOffset.Now;
    public IReadOnlyList<TranscriptEntry> Entries => _entries;
    public int Revision { get; private set; }

    public bool Accept(string snapshot, DateTimeOffset now)
    {
        var text = Normalize(snapshot);
        if (text.Length == 0 || text == _previous) return false;
        var discarded = 0;
        if (_previous.Length == 0)
            Add(text, now);
        else if (text.StartsWith(_previous, StringComparison.Ordinal))
        {
            discarded = _discardedPrefixLength;
            Append(text[_previous.Length..], now);
        }
        else
        {
            var overlap = SuffixPrefixOverlap(_previous, text);
            if (overlap >= MinimumOverlap(_previous, text))
            {
                discarded = Math.Max(0, _discardedPrefixLength - (_previous.Length - overlap));
                Append(text[overlap..], now);
            }
            else if (CommonPrefix(_previous, text) >= MinimumOverlap(_previous, text) || IsLikelyRevision(_previous, text))
            {
                discarded = RevisedDiscardBoundary(_previous, text, _discardedPrefixLength);
                ReviseTail(_previous[_discardedPrefixLength..], text[discarded..], now);
            }
            else
                Add(text, now);
        }
        _previous = text;
        _discardedPrefixLength = discarded;
        Revision++;
        return true;
    }

    public void Break(string reason, DateTimeOffset now)
    {
        // A transient disconnect immediately after clearing must not replay the same buffer.
        if (_entries.Count == 0 && _discardedPrefixLength > 0) return;
        _previous = "";
        _discardedPrefixLength = 0;
        _activeId = null;
        if (_entries.Count == 0 || _entries[^1].IsNotice) return;
        _entries.Add(new(Guid.NewGuid(), now, reason, true));
        Revision++;
    }

    public void Clear(string currentSnapshot = "")
    {
        _entries.Clear(); _previous = Normalize(currentSnapshot); _activeId = null;
        _discardedPrefixLength = _previous.Length;
        Id = Guid.NewGuid(); StartedAt = DateTimeOffset.Now; Revision++;
    }

    public SessionRecord Snapshot() => new(Id, StartedAt, _entries.ToArray());

    private void Add(string text, DateTimeOffset now)
    {
        if (text.Length == 0) return;
        var entry = new TranscriptEntry(Guid.NewGuid(), now, text);
        _entries.Add(entry); _activeId = entry.Id;
    }

    private void Append(string addition, DateTimeOffset now)
    {
        if (addition.Length == 0) return;
        var index = _entries.FindLastIndex(x => x.Id == _activeId);
        if (index < 0) { Add(addition, now); return; }
        var entry = _entries[index];
        if (EndsSentence(entry.Text) && addition.TrimStart().Length > 0 && !char.IsPunctuation(addition.TrimStart()[0]))
            Add(addition.TrimStart(), now);
        else
            _entries[index] = entry with { Text = entry.Text + addition };
    }

    private void ReviseTail(string previous, string current, DateTimeOffset now)
    {
        var index = _entries.FindLastIndex(x => x.Id == _activeId);
        if (index < 0) { Add(current, now); return; }
        var entry = _entries[index];
        if (entry.Text.EndsWith(previous, StringComparison.Ordinal))
            _entries[index] = entry with { Text = entry.Text[..^previous.Length] + current };
        else
        {
            // A native snapshot may include preceding, already segmented sentences.
            var offset = previous.LastIndexOf(entry.Text, StringComparison.Ordinal);
            if (offset >= 0 && CommonPrefix(previous, current) >= offset)
                _entries[index] = entry with { Text = current[offset..] };
            else
                Add(current, now);
        }
    }

    private static bool EndsSentence(string text) => text.TrimEnd().LastOrDefault() is '。' or '！' or '？' or '.' or '!' or '?';
    private static int RevisedDiscardBoundary(string previous, string current, int boundary)
    {
        if (boundary == 0) return 0;
        if (boundary == previous.Length) return current.Length;
        if (CommonPrefix(previous, current) >= boundary) return boundary;
        var suffix = 0;
        while (suffix < Math.Min(previous.Length, current.Length) && previous[^(suffix + 1)] == current[^(suffix + 1)]) suffix++;
        var retained = previous.Length - boundary;
        return suffix >= retained ? current.Length - retained : Math.Min(boundary, current.Length);
    }
    private static int MinimumOverlap(string a, string b) => Math.Min(4, Math.Max(1, Math.Min(a.Length, b.Length) / 2));
    private static bool IsLikelyRevision(string a, string b)
    {
        if (a.Length > 512 || b.Length > 512 || Math.Min(a.Length, b.Length) < 4) return false;
        var limit = Math.Max(1, Math.Min(12, Math.Min(a.Length, b.Length) / 5));
        if (Math.Abs(a.Length - b.Length) > limit) return false;
        int[] row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var diagonal = row[0]; row[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var before = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diagonal + (a[i - 1] == b[j - 1] ? 0 : 1));
                diagonal = before;
            }
        }
        return row[b.Length] <= limit;
    }

    private static int CommonPrefix(string a, string b)
    {
        var i = 0; while (i < a.Length && i < b.Length && a[i] == b[i]) i++; return i;
    }
    private static int SuffixPrefixOverlap(string a, string b)
    {
        // Linear-time overlap: do not perform quadratic scans of the native caption buffer.
        var count = Math.Min(a.Length, b.Length);
        var combined = b + '\0' + a[^count..];
        int[] prefix = new int[combined.Length];
        for (var i = 1; i < combined.Length; i++)
        {
            var j = prefix[i - 1];
            while (j > 0 && combined[i] != combined[j]) j = prefix[j - 1];
            if (combined[i] == combined[j]) j++;
            prefix[i] = j;
        }
        return Math.Min(prefix[^1], b.Length);
    }
    private static string Normalize(string text) => Regex.Replace(text.Replace("\r\n", "\n").Replace('\r', '\n'), @"[\t ]*\n[\t ]*", " ").Trim();
}

public static class TranscriptExport
{
    public static string Format(IEnumerable<TranscriptEntry> entries, bool timestamps = false)
    {
        var builder = new StringBuilder();
        foreach (var entry in entries)
        {
            if (timestamps) builder.Append('[').Append(entry.CapturedAt.ToString("HH:mm:ss")).Append("] ");
            if (entry.IsNotice) builder.Append('〔').Append(entry.Text).Append('〕');
            else builder.Append(entry.Text);
            builder.AppendLine();
        }
        return builder.ToString();
    }
}
