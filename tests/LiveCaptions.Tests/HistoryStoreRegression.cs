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
            store.Clear(new SessionRecord(Guid.NewGuid(), DateTimeOffset.Now, []));
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
            var current = transcript.Snapshot();
            store.Save(current);
            // Startup could not archive an older run. Clearing the current run
            // must leave that run and any unrelated/corrupt files recoverable.
            var previous = session with { Id = Guid.NewGuid() };
            store.Save(previous);
            var previousCheckpoint = store.Load().Sessions.Single(x => x.Record.Id == previous.Id).Path;
            File.WriteAllText(previousCheckpoint + ".tmp", "older interrupted save");
            var currentCheckpoint = store.Load().Sessions.Single(x => x.Record.Id == current.Id).Path;
            File.WriteAllText(currentCheckpoint + ".tmp", "current interrupted save");
            var brokenPath = Path.Combine(root, "broken.json");
            File.WriteAllText(brokenPath, "invalid JSON");
            store.Clear(current);
            Require(File.Exists(legacyJson) && File.Exists(markdown), "Manual clear deleted completed archives");
            Require(!File.Exists(currentCheckpoint) && !File.Exists(currentCheckpoint + ".tmp"), "Clear retained the current checkpoint");
            Require(File.Exists(previousCheckpoint) && File.Exists(previousCheckpoint + ".tmp") && File.ReadAllText(brokenPath) == "invalid JSON",
                "Clear deleted another run or a recovery file");
            // Clearing a session which has not been saved is also safe and repeatable.
            store.Clear(current);
            store.Clear(current with { Id = Guid.NewGuid() });
            store.Save(current);
            Require(store.ArchivePending() == 1, "Unreadable history was not reported");
            Require(File.ReadAllText(brokenPath) == "invalid JSON" && store.Load().Sessions.Count == 0,
                "Unreadable history blocked valid archives or was deleted");
            Require(File.Exists(Path.Combine(store.ArchiveRoot, Path.GetFileNameWithoutExtension(currentCheckpoint) + ".md")) &&
                File.Exists(Path.Combine(store.ArchiveRoot, Path.GetFileNameWithoutExtension(previousCheckpoint) + ".md")),
                "Readable sessions were not archived");
            Require(store.ArchivePending() == 1, "A retry should preserve and report the unreadable file without throwing");
            File.Delete(brokenPath);
            Require(store.ArchivePending() == 0, "Resolved recovery warning did not clear");
            // A temporarily locked file must likewise survive without blocking
            // the other sessions, then be archived after the lock is released.
            store.Save(current);
            store.Save(previous);
            using (new FileStream(previousCheckpoint, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Require(store.ArchivePending() == 1 && !File.Exists(currentCheckpoint), "Locked checkpoint blocked a readable session");
                Require(File.Exists(previousCheckpoint), "Locked checkpoint was lost");
            }
            Require(store.ArchivePending() == 0 && store.Load().Sessions.Count == 0, "Unlocked checkpoint could not be archived");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
