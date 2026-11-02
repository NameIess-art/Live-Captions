using LiveCaptions.Desktop.Storage;

namespace LiveCaptions.Tests;

internal static class PreferencesRegression
{
    internal static void Verify()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "LiveCaptions-preferences-test-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "preferences.json");
        try
        {
            var store = new PreferencesStore(path);
            Require(store.Load().AutoSaveHistory && !Directory.Exists(root), "Default preference should preserve existing autosave without creating files");
            store.Save(new() { AutoSaveHistory = false });
            Require(!new PreferencesStore(path).Load().AutoSaveHistory, "Disabled autosave did not survive a restart");
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { store.Save(new() { AutoSaveHistory = true }); throw new Exception("Settings write failure was ignored"); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                Require(!store.Load().AutoSaveHistory, "Failed settings write lost the saved preference");
            }
            store.Save(new() { AutoSaveHistory = true });
            Require(store.Load().AutoSaveHistory && !File.Exists(path + ".tmp"), "Re-enabling autosave did not commit cleanly");
            foreach (var json in new[] { "{}", "null", "invalid JSON" })
            {
                File.WriteAllText(path, json);
                Require(store.Load().AutoSaveHistory, "Missing or malformed preference did not retain compatible defaults");
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
