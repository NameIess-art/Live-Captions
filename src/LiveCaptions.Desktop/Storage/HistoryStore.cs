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
    public HistoryStore(string? root = null) => Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiveCaptions.Enhanced", "History");
    public void Save(SessionRecord session)
    {
        if (session.Entries.Count == 0) return;
        Directory.CreateDirectory(Root);
        var path = Path.Combine(Root, $"{session.StartedAt:yyyyMMdd-HHmmss}-{session.Id:N}.json");
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(session), new UTF8Encoding(false));
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

    public void Clear()
    {
        if (!Directory.Exists(Root)) return;
        // Only history files in this directory; never recurse into settings or other folders.
        foreach (var path in Directory.EnumerateFiles(Root))
            if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".json.tmp", StringComparison.OrdinalIgnoreCase)) File.Delete(path);
    }
}
