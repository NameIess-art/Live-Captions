using System.Runtime.Versioning;
using Microsoft.Win32;

namespace LiveCaptions.Desktop.Storage;

internal sealed class StartupRegistration(Func<string?> read, Action<string?> write, string executablePath)
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LiveCaptions.Enhanced";

    [SupportedOSPlatform("windows")]
    internal static StartupRegistration ForCurrentUser() => new(
        () =>
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) as string;
        },
        command =>
        {
            if (command == null)
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
                key?.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            else
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
                key.SetValue(ValueName, command, RegistryValueKind.String);
            }
        }, Environment.ProcessPath ?? "");

    internal bool IsEnabled => !string.IsNullOrWhiteSpace(read());

    internal void SetEnabled(bool enabled)
    {
        if (!enabled) { write(null); return; }
        // Run entries have a 260-character limit. Quote the path even without spaces.
        if (!Path.IsPathFullyQualified(executablePath) || executablePath.Contains('"') ||
            !string.Equals(Path.GetExtension(executablePath), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("无法确定应用程序位置，请从发布目录的 EXE 启动后重试。");
        var command = '"' + executablePath + '"';
        if (command.Length > 260)
            throw new InvalidOperationException("应用程序路径过长，请移至较短路径后重试。");
        write(command);
    }
}
