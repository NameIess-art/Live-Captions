using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text;
using System.Windows.Automation;

namespace LiveCaptions.Desktop.Capture;

public sealed record CaptureMessage(string Status, string Text = "", string Source = "", string Detail = "");

internal sealed class NativeCaptionReader
{
    private IntPtr _handle;
    private AutomationElement? _text;
    private AutomationElement? _root;
    private DateTime _lastLookup;
    public CaptureMessage Read()
    {
        // Reuse the live HWND and UIA element. Walking the entire provider tree
        // every three seconds caused regular gaps in subtitle delivery.
        var handle = _handle != IntPtr.Zero && IsWindow(_handle) ? _handle : FindCaptionWindow();
        if (handle == IntPtr.Zero)
        {
            _text = null; _root = null; _handle = IntPtr.Zero;
            return new("waiting", Detail: "请打开 Windows 实时字幕（Win + Ctrl + L）");
        }
        if (handle != _handle || _text == null && DateTime.UtcNow - _lastLookup > TimeSpan.FromMilliseconds(500))
        {
            _handle = handle; _lastLookup = DateTime.UtcNow;
            var root = AutomationElement.FromHandle(handle);
            _root = root;
            _text = root.FindFirst(TreeScope.Descendants, new OrCondition(
                new PropertyCondition(AutomationElement.AutomationIdProperty, "CaptionsTextBlock"),
                new PropertyCondition(AutomationElement.AutomationIdProperty, "CaptionTextBlock")));
        }
        if (_text == null)
        {
            var setup = _root?.FindFirst(TreeScope.Descendants, new OrCondition(
                new PropertyCondition(AutomationElement.AutomationIdProperty, "SpeechModelDropDown"),
                new PropertyCondition(AutomationElement.AutomationIdProperty, "DownloadButton")));
            if (setup != null && !setup.Current.IsOffscreen)
                return new("setup", Source: handle.ToInt64().ToString(), Detail: "请在设置中显示原生字幕并完成语言设置。");
            return new("unsupported", Source: handle.ToInt64().ToString(), Detail: "发现原生字幕窗口，但未找到字幕正文控件；请等待初始化或重新打开原生字幕。");
        }
        var content = _text.Current.Name;
        if (string.IsNullOrEmpty(content) && _text.TryGetCurrentPattern(TextPattern.Pattern, out var pattern))
            content = ((TextPattern)pattern).DocumentRange.GetText(65536);
        return new("connected", content ?? "", handle.ToInt64().ToString());
    }
    public void Reset() { _text = null; _root = null; _handle = IntPtr.Zero; }
    internal static IntPtr FindCaptionWindow()
    {
        var handle = FindWindow("LiveCaptionsDesktopWindow", null);
        if (handle != IntPtr.Zero) return handle;
        var ids = Process.GetProcessesByName("LiveCaptions").Select(p => { using (p) return p.Id; }).ToHashSet();
        EnumWindows((candidate, _) =>
        {
            GetWindowThreadProcessId(candidate, out var id);
            if (ids.Contains((int)id) && IsWindowVisible(candidate)) { handle = candidate; return false; }
            return true;
        }, IntPtr.Zero);
        return handle;
    }
    public static string Diagnose()
    {
        var builder = new StringBuilder();
        EnumWindows((handle, _) =>
        {
            var name = new StringBuilder(256);
            GetClassName(handle, name, name.Capacity);
            GetWindowThreadProcessId(handle, out var pid);
            string process;
            try { using var p = Process.GetProcessById((int)pid); process = p.ProcessName; }
            catch (ArgumentException) { return true; }
            if (process != "LiveCaptions") return true;
            builder.AppendLine($"HWND={handle.ToInt64()} CLASS={name} PROCESS={process}");
            try
            {
                var root = AutomationElement.FromHandle(handle);
                var nodes = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
                for (var i = 0; i < Math.Min(nodes.Count, 150); i++)
                {
                    var node = nodes[i].Current;
                    builder.AppendLine($"{node.ControlType.ProgrammaticName} ID={node.AutomationId} NAME={node.Name}");
                }
            }
            catch (Exception e) { builder.AppendLine(e.GetType().Name + ": " + e.Message); }
            return true;
        }, IntPtr.Zero);
        return builder.ToString();
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr handle, StringBuilder name, int count);
    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr handle);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr handle);
    private delegate bool EnumWindowProc(IntPtr handle, IntPtr param);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowProc callback, IntPtr param);
}
