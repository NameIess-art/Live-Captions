using LiveCaptions.Desktop.Storage;

namespace LiveCaptions.Tests;

internal static class StartupRegression
{
    internal static void Verify()
    {
        string? command = null;
        var executable = Path.Combine(Path.GetTempPath(), "字幕 app", "LiveCaptions.Enhanced.exe");
        StartupRegistration Create() => new(() => command, value => command = value, executable);
        var startup = Create();
        Require(!startup.IsEnabled, "Startup should be opt-in");
        startup.SetEnabled(true);
        Require(command == '"' + executable + '"' && Create().IsEnabled, "Startup command was not quoted or persisted");
        startup.SetEnabled(false);
        startup.SetEnabled(false);
        Require(command == null && !Create().IsEnabled, "Disable did not remove the entry idempotently");
        command = "\"C:\\Old location\\LiveCaptions.Enhanced.exe\"";
        Require(startup.IsEnabled, "Existing registration in a previous location was not detected");
        startup.SetEnabled(true);
        Require(command == '"' + executable + '"', "Enabling did not update the executable location");
        var blocked = new StartupRegistration(() => command, _ => throw new UnauthorizedAccessException("denied"), executable);
        try { blocked.SetEnabled(false); throw new Exception("Write failure was ignored"); }
        catch (UnauthorizedAccessException) { }
        Require(blocked.IsEnabled, "Failed write changed the registered state");
        foreach (var path in new[] { "", "relative.exe", executable + "\"", Path.ChangeExtension(executable, ".dll"),
            Path.Combine(Path.GetTempPath(), new string('a', 260), "app.exe") })
        {
            var invalid = new StartupRegistration(() => command, _ => throw new Exception("Invalid command was written"), path);
            try { invalid.SetEnabled(true); throw new Exception("Invalid executable was accepted"); }
            catch (InvalidOperationException) { }
        }
        // A moved or missing executable must never prevent removing an old registration.
        new StartupRegistration(() => command, value => command = value, "").SetEnabled(false);
        Require(command == null, "Invalid current path prevented disabling startup");
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
