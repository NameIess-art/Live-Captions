using System.Text;
using System.Text.Json;

namespace LiveCaptions.Desktop.Storage;

internal sealed record UserPreferences
{
    public bool AutoSaveHistory { get; init; } = true;
}

internal sealed class PreferencesStore(string path)
{
    internal UserPreferences Load()
    {
        try { return JsonSerializer.Deserialize<UserPreferences>(File.ReadAllText(path)) ?? new(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    internal void Save(UserPreferences preferences)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(preferences, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }
}
