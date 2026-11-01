using LiveCaptions.Core;
using LiveCaptions.Desktop.Storage;

namespace LiveCaptions.Tests;

internal static class HistoryPathRegression
{
    internal static void Verify()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "LiveCaptions-path-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var checkpoints = Path.Combine(root, "checkpoints");
            var settings = Path.Combine(root, "preferences", "history-settings.json");
            var paths = new HistoryPathStore(settings, checkpoints);
            Require(paths.Load() == paths.DefaultPath && !Directory.Exists(root), "Default path should not create directories on startup");
            var chosen = Path.Combine(root, "选择的历史目录");
            var normalized = paths.Save(chosen + Path.DirectorySeparatorChar);
            Require(normalized == chosen && new HistoryPathStore(settings, checkpoints).Load() == chosen,
                "Custom path did not survive a restart");
            var history = new HistoryStore(checkpoints) { ArchiveRoot = paths.Load() };
            var transcript = new Transcript(); transcript.Accept("自定义目录中的字幕", DateTimeOffset.Now);
            history.Save(transcript.Snapshot()); history.ArchivePending();
            Require(Directory.GetFiles(chosen, "*.md").Length == 1 && Directory.GetFiles(chosen, "*.json").Length == 0 &&
                !Directory.Exists(paths.DefaultPath) && history.Load().Sessions.Count == 0, "Archive did not use the selected directory");
            Require(Directory.GetFiles(chosen, "*.tmp").Length == 0, "Write probe was not removed");
            try { paths.Save(checkpoints); throw new InvalidOperationException("Checkpoint directory was accepted"); }
            catch (ArgumentException) { }
            var blocked = Path.Combine(root, "blocked"); File.WriteAllText(blocked, "file");
            try { paths.Save(blocked); throw new InvalidOperationException("Unavailable directory was accepted"); }
            catch (IOException) { }
            Require(paths.Load() == chosen, "Failed update changed the saved preference");
            using (new FileStream(settings, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { paths.Save(paths.DefaultPath); throw new InvalidOperationException("Settings failure was ignored"); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                Require(paths.Load() == chosen, "Settings failure lost the previous preference");
            }
            paths.Save(paths.DefaultPath);
            Require(paths.Load() == paths.DefaultPath && Directory.GetFiles(chosen).Length == 1,
                "Reset moved or deleted existing archives");
            File.WriteAllText(settings, "invalid JSON");
            Require(paths.Load() == paths.DefaultPath, "Malformed settings did not fall back to the default");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
