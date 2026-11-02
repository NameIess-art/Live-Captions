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
        if (test < 0) _window = new MainWindow();
        else
        {
            // UI regressions must never register the test executable for real logon.
            string? startupCommand = null;
            _window = new MainWindow(false,
                Path.Combine(Path.GetDirectoryName(Path.GetFullPath(arguments[test + 1]))!, "winui-test-" + Guid.NewGuid().ToString("N")),
                new Storage.StartupRegistration(() => startupCommand, command => startupCommand = command, Environment.ProcessPath!));
        }
        _window.Activate();
        var dispatcher = _window.DispatcherQueue;
        Program.Instance!.Listen(() => dispatcher.TryEnqueue(_window.ActivateExistingInstance));
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
