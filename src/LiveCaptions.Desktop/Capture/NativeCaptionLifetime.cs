using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace LiveCaptions.Desktop.Capture;

internal static class NativeCaptionLifetime
{
    internal static string ExecutablePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "LiveCaptions.exe");

    internal static bool IsNativeProcess(Process process)
    {
        try
        {
            using var current = Process.GetCurrentProcess();
            _ = process.SafeHandle;
            return !process.HasExited && process.SessionId == current.SessionId &&
                string.Equals(process.MainModule?.FileName, ExecutablePath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        { return false; }
    }

    internal static Process? FindProcess()
    {
        Process? found = null;
        foreach (var process in Process.GetProcessesByName("LiveCaptions"))
        {
            if (found == null && IsNativeProcess(process)) found = process;
            else process.Dispose();
        }
        return found;
    }

    // Keep the suppression leases alive until the native process has actually exited.
    internal static async Task CloseAsync()
    {
        foreach (var process in Process.GetProcessesByName("LiveCaptions"))
        {
            using (process)
            {
                try
                {
                    if (!IsNativeProcess(process)) continue;
                    var window = process.MainWindowHandle;
                    if (GetWindowThreadProcessId(window, out var owner) != 0 && owner == (uint)process.Id)
                        RequestClose(window);
                    else
                        EnumWindows((candidate, _) =>
                        {
                            GetWindowThreadProcessId(candidate, out var id);
                            if (id != (uint)process.Id) return true;
                            var name = new StringBuilder(64);
                            GetClassName(candidate, name, name.Capacity);
                            if (name.ToString() == "LiveCaptionsDesktopWindow") RequestClose(candidate);
                            return true;
                        }, IntPtr.Zero);
                    try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                    catch (TimeoutException)
                    {
                        // A hung native UI must not remain running after the enhanced app closes.
                        if (!process.HasExited) process.Kill();
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or TimeoutException)
                {
                    // An already-exiting or inaccessible process must not prevent saving/closing our UI.
                }
            }
        }
    }

    internal static bool RequestClose(IntPtr handle) => PostMessage(handle, 0x0010, IntPtr.Zero, IntPtr.Zero);
    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
    private delegate bool EnumWindowProc(IntPtr handle, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowProc callback, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr handle, StringBuilder name, int count);
}
