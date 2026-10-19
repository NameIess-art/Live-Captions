using Microsoft.UI.Xaml;

namespace LiveCaptions.Desktop;

public partial class App : Application
{
    private MainWindow? _window;
    public App()
    {
        UnhandledException += (_, e) => Program.ReportFailure(e.Exception);
        // Resolve our own PRI even when launched by an application with package identity.
        ResourceManagerRequested += (_, e) => e.CustomResourceManager =
            new Microsoft.Windows.ApplicationModel.Resources.ResourceManager(Path.Combine(AppContext.BaseDirectory, "resources.pri"));
        InitializeComponent();
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var arguments = Environment.GetCommandLineArgs();
        var test = Array.IndexOf(arguments, "--self-test");
        _window = new MainWindow(test < 0, test < 0 ? null :
            Path.Combine(Path.GetDirectoryName(Path.GetFullPath(arguments[test + 1]))!, "winui-test-" + Guid.NewGuid().ToString("N")));
        _window.Activate();
        if (test >= 0) _window.DispatcherQueue.TryEnqueue(async () =>
        {
            var exitCode = 0;
            try { await _window.VerifyUiAsync(Path.GetFullPath(arguments[test + 1])); }
            catch (Exception ex) { File.WriteAllText(arguments[test + 1], ex.ToString()); exitCode = 1; }
            await _window.ShutdownAsync();
            Environment.ExitCode = exitCode;
        });
    }
}
