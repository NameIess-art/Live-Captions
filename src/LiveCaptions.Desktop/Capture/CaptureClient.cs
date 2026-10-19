using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace LiveCaptions.Desktop.Capture;

/// <summary>Isolates cross-process UIA calls so a stalled provider cannot freeze the window.</summary>
internal sealed class CaptureClient : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private Task? _run;
    private Process? _worker;
    public event Action<CaptureMessage>? Received;
    public void Start() => _run ??= Task.Run(RunAsync);

    [UnconditionalSuppressMessage("SingleFile", "IL3000",
        Justification = "Assembly.Location is used only under the dotnet host; single-file apphosts relaunch Environment.ProcessPath directly.")]
    private async Task RunAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var start = new ProcessStartInfo(Environment.ProcessPath!)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8
                };
                if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
                    start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
                start.ArgumentList.Add("--worker");
                using var process = Process.Start(start) ?? throw new IOException("采集进程启动失败");
                _worker = process;
                // Drain stderr without keeping potentially unbounded diagnostic data.
                process.ErrorDataReceived += (_, _) => { };
                process.BeginErrorReadLine();
                try
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        var line = await process.StandardOutput.ReadLineAsync(_stop.Token).AsTask()
                            .WaitAsync(TimeSpan.FromSeconds(8), _stop.Token);
                        if (line == null) throw new IOException("采集进程已退出");
                        var message = JsonSerializer.Deserialize<CaptureMessage>(line);
                        if (message != null) Received?.Invoke(message);
                    }
                }
                finally
                {
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                    _worker = null;
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or TimeoutException or JsonException or System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                Received?.Invoke(new("error", Detail: "采集连接中断，正在自动重连。"));
                try { await Task.Delay(1500, _stop.Token); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try { if (_worker is { HasExited: false }) _worker.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        if (_run != null) await _run;
        _stop.Dispose();
    }
}
