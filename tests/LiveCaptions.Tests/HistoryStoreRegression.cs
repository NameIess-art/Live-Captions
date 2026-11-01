using LiveCaptions.Core;
using LiveCaptions.Desktop.Storage;

namespace LiveCaptions.Tests;

internal static class HistoryStoreRegression
{
    internal static void Verify()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "LiveCaptions-history-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new HistoryStore(root);
            store.ArchivePending();
            Require(!Directory.Exists(root), "Empty startup created an archive");
            var transcript = new Transcript();
            transcript.Accept("本次字幕 **原文**", DateTimeOffset.Now);
            var session = transcript.Snapshot();
            store.Save(session);
            // Simulate an unavailable archive destination. No checkpoint may be lost.
            File.WriteAllText(store.ArchiveRoot, "blocked");
            try { store.ArchivePending(); throw new InvalidOperationException("Archive failure was ignored"); }
            catch (IOException) { }
            Require(store.Load().Sessions.Single().Record.Id == session.Id, "Failed archive lost the checkpoint");
            File.Delete(store.ArchiveRoot);
            Directory.CreateDirectory(store.ArchiveRoot);
            var checkpoint = store.Load().Sessions.Single().Path;
            var blockedMarkdown = Path.Combine(store.ArchiveRoot, Path.GetFileNameWithoutExtension(checkpoint) + ".md");
            Directory.CreateDirectory(blockedMarkdown);
            try { store.ArchivePending(); throw new InvalidOperationException("Markdown archive failure was ignored"); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            Require(File.Exists(checkpoint), "Markdown failure deleted the checkpoint");
            Directory.Delete(blockedMarkdown);
            store.ArchivePending();
            Require(new HistoryStore(root).Load().Sessions.Count == 0, "Reopened history includes the previous run");
            var markdown = Directory.GetFiles(store.ArchiveRoot, "*.md").Single();
            Require(File.ReadAllText(markdown) == HistoryDocument.Markdown([session]), "Readable archive differs from the transcript");
            Require(Directory.GetFiles(store.ArchiveRoot).Length == 1, "Archive contains files other than Markdown");
            // An interrupted close may leave both Markdown and its checkpoint.
            store.Save(session);
            store.ArchivePending();
            Require(Directory.GetFiles(store.ArchiveRoot).Length == 1, "Retry duplicated the archive");
            var legacyJson = Path.Combine(store.ArchiveRoot, "legacy.json");
            File.WriteAllText(legacyJson, "Previous version's backup");
            transcript.Clear();
            transcript.Accept("下一次字幕", DateTimeOffset.Now);
            store.Save(transcript.Snapshot());
            store.Clear();
            Require(File.Exists(legacyJson) && File.Exists(markdown), "Manual clear deleted completed archives");
            store.Save(transcript.Snapshot());
            File.WriteAllText(Path.Combine(root, "broken.json"), "invalid JSON");
            try { store.ArchivePending(); throw new InvalidOperationException("Unreadable history was ignored"); }
            catch (IOException) { }
            Require(File.Exists(Path.Combine(root, "broken.json")) && store.Load().Sessions.Count == 1,
                "Unreadable history caused data loss");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
