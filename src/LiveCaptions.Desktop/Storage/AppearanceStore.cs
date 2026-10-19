using System.IO;
using System.Text;
using System.Text.Json;

namespace LiveCaptions.Desktop.Storage;

internal sealed class AppearanceStore(string path)
{
    internal CaptionAppearance Load()
    {
        try
        {
            return (JsonSerializer.Deserialize<CaptionAppearance>(File.ReadAllText(path)) ?? new()).Normalize();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    internal void Save(CaptionAppearance appearance)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(appearance.Normalize(), new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }
}
