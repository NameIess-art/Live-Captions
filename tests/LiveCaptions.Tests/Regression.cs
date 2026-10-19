using LiveCaptions.Core;

namespace LiveCaptions.Tests;

internal static class Regression
{
    public static int Run()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("all history is chronological and the current saved session appears once", () =>
            {
                var now = DateTimeOffset.Now;
                var id = Guid.NewGuid();
                var old = new SessionRecord(Guid.NewGuid(), now.AddDays(-1), [new(Guid.NewGuid(), now.AddDays(-1), "旧会话")]);
                var saved = new SessionRecord(id, now, [new(Guid.NewGuid(), now, "未修正")]);
                var live = new SessionRecord(id, now, [new(Guid.NewGuid(), now, "当前会话")]);
                Assert(HistoryDocument.PlainText([saved, old, live]) == "旧会话" + Environment.NewLine + "当前会话" + Environment.NewLine, "history missing or duplicated");
            }),
            ("repeated speech across sessions is retained and status is omitted", () =>
            {
                var now = DateTimeOffset.Now;
                var first = new SessionRecord(Guid.NewGuid(), now, [new(Guid.NewGuid(), now, "同一句话"), new(Guid.NewGuid(), now, "采集中断", true)]);
                var second = new SessionRecord(Guid.NewGuid(), now.AddHours(1), [new(Guid.NewGuid(), now, "同一句话")]);
                Assert(HistoryDocument.PlainText([first, second]) == "同一句话" + Environment.NewLine + "同一句话" + Environment.NewLine, "utterances dropped or status leaked");
            }),
            ("Markdown export escapes recognized Markdown and HTML as literal speech", () =>
            {
                var now = DateTimeOffset.Now;
                var session = new SessionRecord(Guid.NewGuid(), now, [new(Guid.NewGuid(), now, "# 标题\n1. 一项\n**文字** <script> &copy;")]);
                var text = HistoryDocument.Markdown([session]);
                Assert(text.StartsWith("# 字幕历史\n\n## ") && text.Contains("\\# 标题") && text.Contains("1\\. 一项") && text.Contains("\\*\\*文字\\*\\*") && text.Contains("\\<script\\>") && text.Contains("&amp;copy;"), "Markdown changed recognized text");
            }),
            ("incremental recognition stays one entry", () => Check(["今", "今天", "今天天气很好。"], "今天天气很好。")),
            ("identical polling snapshots are ignored", () => Check(["こんにちは", "こんにちは", "こんにちは"], "こんにちは")),
            ("recognition correction replaces trailing words", () => Check(["今天气很好", "今天天气很好", "今天天气真好。"], "今天天气真好。")),
            ("rolling native buffer retains scrolled text", () => Check(["这是第一句话。这是第二句话", "这是第二句话。还有第三句话。"], "这是第一句话。这是第二句话。还有第三句话。")),
            ("correction after scrolling retains old prefix", () => Check(["之前内容。今天下雨了", "今天下雨了我们", "今天下雪了我们"], "之前内容。今天下雪了我们")),
            ("sentence boundaries do not duplicate earlier text", () => Check(["第一句话。", "第一句话。第二句错", "第一句话。第二句对。"], "第一句话。第二句对。")),
            ("spoken repeated sentences are retained", () => Check(["你好。", "你好。你好。", "你好。你好。再见。"], "你好。你好。再见。")),
            ("unrelated new sentence is appended", () => Check(["今日はいい天気です。", "明日は雨でしょう。"], "今日はいい天気です。明日は雨でしょう。")),
            ("blank snapshots do not erase captions", () => Check(["hello world", "", "  ", "hello world!"], "hello world!")),
            ("whitespace and line endings are normalized", () => Check(["first\r\nsecond", "first\nsecond"], "first second")),
            ("reconnect separates identical utterances", () =>
            {
                var transcript = new Transcript(); var now = DateTimeOffset.Now;
                transcript.Accept("同一句话。", now); transcript.Break("连接中断", now); transcript.Accept("同一句话。", now);
                Assert(transcript.Entries.Count == 3 && transcript.Entries[1].IsNotice, "missing reconnect boundary");
            }),
            ("session reset clears deduplication state", () =>
            {
                var transcript = new Transcript(); var id = transcript.Id;
                transcript.Accept("原有字幕", DateTimeOffset.Now); transcript.Clear(); transcript.Accept("原有字幕", DateTimeOffset.Now);
                Assert(transcript.Id != id && transcript.Entries.Count == 1, "session not reset");
            }),
            ("cleared native buffer is not replayed and new speech is recorded", () =>
            {
                var t = new Transcript(); var now = DateTimeOffset.Now;
                t.Accept("清空前的字幕。", now); var oldId = t.Id;
                t.Clear("清空前的字幕。");
                t.Accept("清空前的字幕。", now);
                t.Break("暂时断开", now); t.Accept("清空前的字幕。", now);
                Assert(t.Id != oldId && t.Entries.Count == 0, "cleared buffer returned");
                t.Accept("清空前的字幕。新的内容", now);
                t.Accept("清空前的字幕。新的内容。", now);
                Assert(TranscriptExport.Format(t.Entries).Trim() == "新的内容。", "new speech missing or old prefix replayed");
            }),
            ("corrections to cleared text stay discarded before new speech", () =>
            {
                var t = new Transcript(); var now = DateTimeOffset.Now;
                t.Accept("今天下雨了", now); t.Clear("今天下雨了");
                t.Accept("今天下雪了", now);
                Assert(t.Entries.Count == 0, "old recognition correction returned");
                t.Accept("今天下雪了我们出门", now);
                t.Accept("今天下雪了我们回家", now);
                Assert(TranscriptExport.Format(t.Entries).Trim() == "我们回家", "new speech revision lost the clear boundary");
            }),
            ("rolling captions and corrections before clear boundary retain only new text", () =>
            {
                var t = new Transcript(); var now = DateTimeOffset.Now;
                t.Clear("旧的开场。今天下雨了");
                t.Accept("旧的开场。今天下雨了我们出门", now);
                t.Accept("今天下雨了我们出门", now);
                t.Accept("今天下雪了我们出门", now);
                t.Accept("今天下雪了我们出门。然后回家。", now);
                Assert(string.Concat(t.Entries.Select(x => x.Text)) == "我们出门。然后回家。", "rolling old text reappeared");
            }),
            ("snapshot is immutable during later revisions", () =>
            {
                var transcript = new Transcript(); transcript.Accept("hello", DateTimeOffset.Now); var snapshot = transcript.Snapshot();
                transcript.Accept("hello world", DateTimeOffset.Now);
                Assert(snapshot.Entries.Single().Text == "hello", "snapshot mutated");
            }),
            ("timestamps are optional and notices are explicit", () =>
            {
                var entry = new TranscriptEntry(Guid.NewGuid(), new DateTimeOffset(2026, 10, 17, 12, 34, 56, TimeSpan.FromHours(8)), "字幕");
                Assert(TranscriptExport.Format([entry]).Trim() == "字幕", "plain export changed");
                Assert(TranscriptExport.Format([entry], true).Trim() == "[12:34:56] 字幕", "timestamp incorrect");
            }),
            ("large moving buffer is processed without repeated text", () =>
            {
                var prefix = new string('甲', 20000); var rolling = new string('乙', 20000);
                Check([prefix + rolling, rolling + "新内容"], prefix + rolling + "新内容");
            }),
        };
        var failed = 0;
        foreach (var (name, test) in tests)
        {
            try { test(); Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
        }
        Console.WriteLine($"{tests.Length - failed}/{tests.Length} passed");
        return failed == 0 ? 0 : 1;
    }

    private static void Check(string[] snapshots, string expected)
    {
        var transcript = new Transcript();
        foreach (var snapshot in snapshots) transcript.Accept(snapshot, DateTimeOffset.Now);
        var actual = string.Concat(transcript.Entries.Where(x => !x.IsNotice).Select(x => x.Text));
        Assert(actual == expected, $"expected <{expected}>, got <{actual}>");
    }
    private static void Assert(bool result, string message) { if (!result) throw new InvalidOperationException(message); }
}
