using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;

namespace LiveCaptions.Desktop.Capture;

internal sealed record CaptionLanguage(string Name, bool? Installed);
internal sealed record LanguageSettingsResult(bool Success, string Current = "", CaptionLanguage[]? Languages = null, string Detail = "");

internal static class NativeLanguageSettings
{
    [UnconditionalSuppressMessage("SingleFile", "IL3000",
        Justification = "Assembly.Location is used only under the dotnet host; single-file apphosts relaunch Environment.ProcessPath directly.")]
    public static async Task<LanguageSettingsResult> RunAsync(string? language = null)
    {
        var handle = NativeCaptionReader.FindCaptionWindow();
        if (handle == IntPtr.Zero) return new(false, Detail: "请先启动 Windows 实时字幕并完成初始化。");
        try
        {
            // Owned by the UI process, so timeout/worker failure still restores native surfaces.
            using var surfaces = new NativeLanguageSurfaces(handle);
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            start.ArgumentList.Add("--language-settings");
            if (language != null) start.ArgumentList.Add(language);
            using var process = Process.Start(start) ?? throw new IOException("语言操作进程启动失败。");
            process.ErrorDataReceived += (_, _) => { };
            process.BeginErrorReadLine();
            try
            {
                var json = await process.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(12));
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
                return JsonSerializer.Deserialize<LanguageSettingsResult>(json) ?? new(false, Detail: "无法读取识别语言，请重试。");
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or JsonException or
                                       System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new(false, Detail: "语言操作未完成，请重试。");
        }
    }

    internal static LanguageSettingsResult Execute(string? language)
    {
        AutomationElement? root = null;
        string original = "";
        var entered = false;
        try
        {
            var handle = NativeCaptionReader.FindCaptionWindow();
            if (handle == IntPtr.Zero) return new(false, Detail: "未找到 Windows 实时字幕。");
            root = AutomationElement.FromHandle(handle);
            if (Find(root, "SpeechModelDropDown") != null)
                return new(false, Detail: "请先完成当前的系统语言设置，再使用增强窗口切换。");
            Invoke(WaitFor(root, "SettingsButton"));
            Invoke(WaitFor(root, "ChangeLanguageMenuFlyoutItem"));
            entered = true;
            var selector = WaitFor(root, "SpeechModelDropDown") ?? throw new InvalidOperationException();
            original = Selected(selector);
            if (original.Length == 0) throw new InvalidOperationException();
            Expand(selector);
            var items = selector.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
            var choices = new List<CaptionLanguage>();
            AutomationElement? target = null;
            foreach (AutomationElement item in items)
            {
                var name = item.Current.Name;
                if (string.IsNullOrWhiteSpace(name)) continue;
                choices.Add(new(name, name == original ? true : Installed(item)));
                if (name == language) target = item;
            }
            if (choices.Count == 0) throw new InvalidOperationException();
            var languages = choices.DistinctBy(x => x.Name).ToArray();
            if (language == null || language == original)
                return new(true, original, languages);
            if (target == null) return new(false, original, languages, "该语言已不可用，请重新打开语言菜单。");
            if (languages.First(x => x.Name == language).Installed == false)
                return new(false, original, languages, "该语言尚未安装，请先安装对应的 Windows 实时字幕语言包。");
            Select(target);
            Collapse(selector);
            Thread.Sleep(200);
            if (Find(root, "DownloadButton") != null || Find(root, "SelectionReadyToCaptionTextBlock") == null)
                return new(false, original, languages, "该语言需要先下载语言包，当前识别语言未更改。");
            Invoke(WaitFor(root, "ContinueButton"));
            var ready = WaitFor(root, "ReadyToCaptionTextBlock", 4000);
            if (ready == null || !ready.Current.Name.Contains(language, StringComparison.Ordinal))
                return new(false, original, languages, "尚未确认语言切换完成，请重新打开菜单检查。");
            entered = false;
            return new(true, language, languages);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new(false, original, Detail: "当前 Windows 语言控件暂不可用，请重试。");
        }
        finally
        {
            // Listing/cancelling/uninstalled choices leave recognition in its original language.
            // No keyboard input, focus changes, or foreground activation.
            if (entered && root != null)
            {
                try
                {
                    var selector = Find(root, "SpeechModelDropDown");
                    if (selector != null)
                    {
                        Expand(selector);
                        if (original.Length > 0)
                        {
                            var item = selector.FindFirst(TreeScope.Descendants,
                                new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                                    new PropertyCondition(AutomationElement.NameProperty, original)));
                            if (item != null) Select(item);
                        }
                        Collapse(selector);
                        Invoke(WaitFor(root, "ContinueButton"));
                        WaitFor(root, "ReadyToCaptionTextBlock");
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { }
            }
        }
    }

    private static bool? Installed(AutomationElement item)
    {
        var text = item.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
        if (text?.TryGetCurrentPattern(TextPattern.Pattern, out var pattern) != true) return null;
        var weight = ((TextPattern)pattern).DocumentRange.GetAttributeValue(TextPattern.FontWeightAttribute);
        return weight is int value ? value >= 600 : null;
    }

    private static string Selected(AutomationElement selector) =>
        selector.TryGetCurrentPattern(SelectionPattern.Pattern, out var pattern)
            ? ((SelectionPattern)pattern).Current.GetSelection().FirstOrDefault()?.Current.Name ?? "" : "";
    private static void Expand(AutomationElement selector) =>
        ((ExpandCollapsePattern)selector.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
    private static void Collapse(AutomationElement selector) =>
        ((ExpandCollapsePattern)selector.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Collapse();
    private static void Select(AutomationElement item) =>
        ((SelectionItemPattern)item.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
    private static void Invoke(AutomationElement? element)
    {
        if (element == null) throw new InvalidOperationException();
        ((InvokePattern)element.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
    }
    private static AutomationElement? Find(AutomationElement root, string id) => root.FindFirst(
        TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id));
    private static AutomationElement? WaitFor(AutomationElement root, string id, int timeout = 2000)
    {
        var clock = Stopwatch.StartNew();
        do
        {
            var element = Find(root, id);
            if (element != null) return element;
            Thread.Sleep(80);
        } while (clock.ElapsedMilliseconds < timeout);
        return null;
    }
}
