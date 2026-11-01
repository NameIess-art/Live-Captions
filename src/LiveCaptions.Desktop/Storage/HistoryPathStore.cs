using System.Text;
using System.Text.Json;

namespace LiveCaptions.Desktop.Storage;

internal sealed class HistoryPathStore(string settingsPath, string checkpointRoot)
{
    private sealed record Settings(string? ArchivePath);
    public string DefaultPath => Path.Combine(checkpointRoot, "Archives");

    public string Load()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(settingsPath));
            return Normalize(settings?.ArchivePath ?? DefaultPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        { return DefaultPath; }
    }

    public string Save(string selectedPath)
    {
        var normalized = Normalize(selectedPath);
        Directory.CreateDirectory(normalized);
        // Check the actual ability to create files before changing the saved preference.
        var probe = Path.Combine(normalized, ".livecaptions-" + Guid.NewGuid().ToString("N") + ".tmp");
        using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        var temporary = settingsPath + ".tmp";
        var useDefault = string.Equals(normalized, Path.TrimEndingDirectorySeparator(Path.GetFullPath(DefaultPath)), StringComparison.OrdinalIgnoreCase);
        File.WriteAllText(temporary, JsonSerializer.Serialize(new Settings(useDefault ? null : normalized)), new UTF8Encoding(false));
        File.Move(temporary, settingsPath, overwrite: true);
        return normalized;
    }

    private string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("请选择完整的文件夹路径。");
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (string.Equals(normalized, Path.TrimEndingDirectorySeparator(Path.GetFullPath(checkpointRoot)), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("此目录用于临时保存，请选择它的子文件夹或其他文件夹。");
        return normalized;
    }
}
