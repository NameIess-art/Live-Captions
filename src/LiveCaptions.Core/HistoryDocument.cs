using System.Text;
using System.Text.RegularExpressions;

namespace LiveCaptions.Core;

public static class HistoryDocument
{
    public static IReadOnlyList<SessionRecord> Sessions(IEnumerable<SessionRecord> sessions) => sessions
        .GroupBy(x => x.Id).Select(x => x.Last()).OrderBy(x => x.StartedAt).ToArray();

    public static string PlainText(IEnumerable<SessionRecord> sessions) => TranscriptExport.Format(
        Sessions(sessions).SelectMany(x => x.Entries).Where(x => !x.IsNotice));

    public static string Markdown(IEnumerable<SessionRecord> sessions)
    {
        var builder = new StringBuilder("# 字幕历史\n\n");
        foreach (var session in Sessions(sessions))
        {
            var entries = session.Entries.Where(x => !x.IsNotice).ToArray();
            if (entries.Length == 0) continue;
            builder.Append("## ").Append(session.StartedAt.ToString("yyyy-MM-dd HH:mm:ss zzz")).Append("\n\n");
            foreach (var entry in entries)
            {
                // Keep recognized speech as literal paragraphs, including Markdown/HTML-like speech.
                var escaped = Regex.Replace(entry.Text.Replace("&", "&amp;"), @"[\\`*_{}\[\]<>|]", @"\$0");
                escaped = Regex.Replace(escaped, @"(?m)^(\s*)([#+\-=])", "$1\\$2");
                escaped = Regex.Replace(escaped, @"(?m)^(\s*\d+)([.)])(?=\s)", "$1\\$2");
                builder.Append(escaped.Replace("\r\n", "\n").Replace('\r', '\n')).Append("\n\n");
            }
        }
        return builder.ToString();
    }
}
