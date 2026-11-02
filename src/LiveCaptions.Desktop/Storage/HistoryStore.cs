using System.IO;
using System.Text;
using System.Text.Json;
using LiveCaptions.Core;

namespace LiveCaptions.Desktop.Storage;

internal sealed record SavedSession(string Path, SessionRecord Record)
{
    public string Label => $"{Record.StartedAt.LocalDateTime:MM-dd HH:mm}  ·  {Record.Entries.Count(x => !x.IsNotice)} 段";
}

internal sealed class HistoryStore
{
    public string Root { get; }
    public string ArchiveRoot { get; set; }
    public HistoryStore(string? root = null)
    {
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiveCaptions.Enhanced", "History");
        ArchiveRoot = Path.Combine(Root, "Archives");
    }
    public void Save(SessionRecord session)
    {
        if (session.Entries.Count == 0) return;
        Directory.CreateDirectory(Root);
        var path = CheckpointPath(session);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(session), new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }

    // Unreadable checkpoints stay in place for recovery. They must not prevent
    // readable sessions from being archived or keep the application open forever.
    public int ArchivePending()
    {
        var loaded = Load();
        foreach (var saved in loaded.Sessions)
        {
            Directory.CreateDirectory(ArchiveRoot);
            var archive = Path.Combine(ArchiveRoot, Path.GetFileNameWithoutExtension(saved.Path) + ".md");
            // Remove the checkpoint only after Markdown is saved. Stable names make
            // retries safe if a previous exit was interrupted.
            WriteAtomic(archive, HistoryDocument.Markdown([saved.Record]));
            File.Delete(saved.Path);
        }
        return loaded.Unreadable;
    }

    private static void WriteAtomic(string path, string content)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, content, new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }

    public (IReadOnlyList<SavedSession> Sessions, int Unreadable) Load()
    {
        if (!Directory.Exists(Root)) return ([], 0);
        var sessions = new List<SavedSession>();
        var unreadable = 0;
        foreach (var path in Directory.EnumerateFiles(Root, "*.json").OrderDescending())
        {
            try
            {
                var session = JsonSerializer.Deserialize<SessionRecord>(File.ReadAllText(path));
                if (session == null || session.Entries == null || session.Entries.Any(x => x == null || x.Text == null))
                { unreadable++; continue; }
                sessions.Add(new(path, session));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { unreadable++; }
        }
        return (sessions, unreadable);
    }

    private string CheckpointPath(SessionRecord session) =>
        Path.Combine(Root, $"{session.StartedAt:yyyyMMdd-HHmmss}-{session.Id:N}.json");

    public void Clear(SessionRecord session)
    {
        if (!Directory.Exists(Root)) return;
        // A failed startup archive can leave checkpoints belonging to older runs.
        // Clear only this session, including an interrupted write of its checkpoint.
        var path = CheckpointPath(session);
        File.Delete(path);
        File.Delete(path + ".tmp");
    }
}
