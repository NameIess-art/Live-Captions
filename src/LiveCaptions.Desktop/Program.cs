using System.Text;
using System.Text.Json;
using LiveCaptions.Desktop.Capture;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace LiveCaptions.Desktop;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportFailure(e.ExceptionObject as Exception);
        // UIA workers never initialize XAML or create application windows.
        if (args.Contains("--worker") || args.Contains("--diagnose") || args.Contains("--language-settings"))
        {
            var result = 0;
            var worker = new Thread(() => result = RunWorker(args));
            worker.SetApartmentState(ApartmentState.MTA);
            worker.Start(); worker.Join();
            return result;
        }
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(parameter =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
        return Environment.ExitCode;
    }

    internal static void ReportFailure(Exception? exception)
    {
        var args = Environment.GetCommandLineArgs();
        var test = Array.IndexOf(args, "--self-test");
        var path = test >= 0 && test + 1 < args.Length ? args[test + 1] :
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiveCaptions.Enhanced", "last-error.txt");
        try { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!); File.WriteAllText(path, exception?.ToString() ?? "Unknown application failure"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static int RunWorker(string[] args)
    {
        var language = Array.IndexOf(args, "--language-settings");
        if (language >= 0)
        {
            var result = NativeLanguageSettings.Execute(language + 1 < args.Length ? args[language + 1] : null);
            using var writer = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
            writer.WriteLine(JsonSerializer.Serialize(result));
            return result.Success ? 0 : 1;
        }
        var diagnose = Array.IndexOf(args, "--diagnose");
        if (diagnose >= 0)
        {
            File.WriteAllText(diagnose + 1 < args.Length ? args[diagnose + 1] : "caption-diagnostic.txt",
                NativeCaptionReader.Diagnose(), Encoding.UTF8);
            return 0;
        }
        using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        var reader = new NativeCaptionReader();
        CaptureMessage? previous = null;
        var lastSent = DateTime.MinValue;
        while (true)
        {
            CaptureMessage message;
            try { message = reader.Read(); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { reader.Reset(); message = new("error", Detail: "字幕读取暂时中断：" + ex.GetType().Name); }
            try
            {
                // Deliver changes immediately; unchanged content only needs a heartbeat.
                if (message != previous || DateTime.UtcNow - lastSent >= TimeSpan.FromSeconds(2))
                {
                    output.WriteLine(JsonSerializer.Serialize(message));
                    previous = message; lastSent = DateTime.UtcNow;
                }
            }
            catch (IOException) { return 0; }
            Thread.Sleep(message.Status == "connected" ? 60 : 250);
        }
    }
}
