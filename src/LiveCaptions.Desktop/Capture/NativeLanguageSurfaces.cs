using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace LiveCaptions.Desktop.Capture;

/// <summary>UI-dispatcher-owned leases sharing the original native surface state.</summary>
internal sealed class NativeLanguageSurfaces : IDisposable
{
    private static readonly Dictionary<uint, Surfaces> Active = [];
    private Surfaces? _surfaces;

    internal NativeLanguageSurfaces(IntPtr root) : this(OwnerOf(root), root) { }

    // Install the process hook before its first window or caption text exists.
    internal NativeLanguageSurfaces(uint process) : this(process, IntPtr.Zero) { }

    private static uint OwnerOf(IntPtr root)
    {
        if (GetWindowThreadProcessId(root, out var process) == 0)
            throw new InvalidOperationException("原生字幕窗口已失效。");
        return process;
    }

    private NativeLanguageSurfaces(uint process, IntPtr root)
    {
        if (!Active.TryGetValue(process, out var surfaces) || !surfaces.IsAlive)
        {
            surfaces = new Surfaces(process);
            Active[process] = surfaces;
        }
        _surfaces = surfaces;
        surfaces.Leases++;
        try { surfaces.Refresh(root); }
        catch { Dispose(); throw; }
    }

    internal bool Owns(IntPtr window) => _surfaces is { IsAlive: true } surfaces &&
        GetWindowThreadProcessId(window, out var process) != 0 && process == surfaces.ProcessId;
    internal bool Owns(uint process) => _surfaces is { IsAlive: true } surfaces && process == surfaces.ProcessId;
    internal bool IsAlive => _surfaces is { IsAlive: true };
    internal void Refresh() => _surfaces?.Refresh(IntPtr.Zero);

    internal void Refresh(IntPtr root)
    {
        if (!Owns(root)) throw new InvalidOperationException("原生字幕窗口已更换。");
        _surfaces!.Refresh(root);
    }

    public void Dispose()
    {
        var surfaces = _surfaces;
        _surfaces = null;
        if (surfaces == null || --surfaces.Leases != 0) return;
        if (Active.TryGetValue(surfaces.ProcessId, out var active) && ReferenceEquals(active, surfaces))
            Active.Remove(surfaces.ProcessId);
        surfaces.Dispose();
    }

    internal static void RestoreAll()
    {
        foreach (var surfaces in Active.Values) surfaces.Dispose();
        Active.Clear();
    }

    private sealed class Surfaces : IDisposable
    {
        private const int ExStyle = -20;
        private const long Layered = 0x80000, Transparent = 0x20, NoActivate = 0x08000000;
        private const long ToolWindow = 0x80, AppWindow = 0x40000;
        private const long ManagedStyles = Layered | Transparent | NoActivate | ToolWindow | AppWindow;
        private const uint AlphaFlag = 2;
        private readonly Process _owner;
        private readonly Dictionary<IntPtr, Surface> _saved = [];
        private readonly HashSet<IntPtr> _roots = [];
        private readonly WinEventProc _callback;
        private readonly IntPtr _hook;
        private bool _disposed;
        private string _lastFailure = "";
        private sealed record Surface(IntPtr Style, uint Color, byte Alpha, uint Flags,
            bool HasLayerAttributes, bool Root, bool WasMinimized);
        internal uint ProcessId { get; }
        internal int Leases { get; set; }
        internal bool IsAlive => !_disposed && !_owner.HasExited;

        internal Surfaces(uint process)
        {
            ProcessId = process;
            _owner = Process.GetProcessById((int)process);
            try { _ = _owner.SafeHandle; }
            catch { _owner.Dispose(); throw; }
            _callback = (_, eventType, window, objectId, child, _, _) =>
            {
                if (_disposed || objectId != 0 || child != 0) return;
                if (eventType == 0x8001) { _saved.Remove(window); _roots.Remove(window); }
                else Suppress(window);
            };
            _hook = SetWinEventHook(0x8000, 0x8002, IntPtr.Zero, _callback, process, 0, 0);
            if (_hook == IntPtr.Zero)
            {
                _owner.Dispose();
                throw new InvalidOperationException("无法隐藏原生字幕窗口。");
            }
        }

        internal void Refresh(IntPtr root)
        {
            if (!IsAlive)
            {
                if (root == IntPtr.Zero) return;
                throw new InvalidOperationException("原生字幕进程已退出。");
            }
            if (root != IntPtr.Zero) _roots.Add(root);
            EnumWindows((window, _) => { Suppress(window); return true; }, IntPtr.Zero);
            if (root != IntPtr.Zero && !Suppress(root))
                throw new InvalidOperationException("无法隐藏原生字幕窗口：" + _lastFailure);
        }

        private bool Suppress(IntPtr window)
        {
            if (GetWindowThreadProcessId(window, out var process) == 0 || process != ProcessId ||
                GetAncestor(window, 2) != window || !IsAlive) return false;
            var isRoot = _roots.Contains(window);
            if (!isRoot)
            {
                var className = new StringBuilder(64);
                GetClassName(window, className, className.Capacity);
                isRoot = className.ToString() == "LiveCaptionsDesktopWindow";
                if (isRoot) _roots.Add(window);
            }
            var style = GetWindowLongPtr(window, ExStyle);
            if (!_saved.ContainsKey(window))
            {
                var color = 0u; byte alpha = 255; var flags = AlphaFlag;
                // Per-pixel layered windows have no constant-alpha attributes. Do not
                // reject a fresh LiveCaptions window just because this query returns false.
                var hasAttributes = (style.ToInt64() & Layered) != 0 &&
                    GetLayeredWindowAttributes(window, out color, out alpha, out flags);
                _saved[window] = new(style, color, alpha, flags, hasAttributes, isRoot, IsIconic(window));
            }
            else if (isRoot && !_saved[window].Root)
                _saved[window] = _saved[window] with { Root = true, WasMinimized = IsIconic(window) };
            var desired = new IntPtr((style.ToInt64() | Layered | Transparent | NoActivate | ToolWindow) & ~AppWindow);
            if (style != desired)
            {
                Marshal.SetLastPInvokeError(0);
                var changed = SetWindowLongPtr(window, ExStyle, desired);
                if (changed == IntPtr.Zero && Marshal.GetLastPInvokeError() != 0)
                    return Fail("设置窗口样式失败，error=" + Marshal.GetLastPInvokeError());
            }
            if (!GetLayeredWindowAttributes(window, out _, out var currentAlpha, out var currentFlags) ||
                currentAlpha != 0 || currentFlags != AlphaFlag)
            {
                if (!SetLayeredWindowAttributes(window, 0, 0, AlphaFlag))
                    return Fail("设置透明度失败，error=" + Marshal.GetLastPInvokeError());
            }
            // LiveCaptions can overwrite its alpha. Minimize the root without activation
            // so those resets cannot reveal it. UIA recognition remains available.
            if (isRoot && !IsIconic(window)) ShowWindow(window, 7); // SW_SHOWMINNOACTIVE
            return true;
        }

        private bool Fail(string reason) { _lastFailure = reason; return false; }

        public void Dispose()
        {
            if (_disposed) return;
            var alive = IsAlive;
            _disposed = true;
            UnhookWinEvent(_hook);
            if (alive)
                foreach (var (window, saved) in _saved)
                {
                    if (GetWindowThreadProcessId(window, out var process) == 0 || process != ProcessId) continue;
                    if (saved.Root && !saved.WasMinimized) ShowWindow(window, 4); // SW_SHOWNOACTIVATE
                    if (saved.HasLayerAttributes)
                        SetLayeredWindowAttributes(window, saved.Color, saved.Alpha, saved.Flags);
                    var style = GetWindowLongPtr(window, ExStyle).ToInt64();
                    var restored = (style & ~ManagedStyles) | (saved.Style.ToInt64() & ManagedStyles);
                    if ((saved.Style.ToInt64() & Layered) != 0 && !saved.HasLayerAttributes)
                        // Allow UpdateLayeredWindow again after constant-alpha suppression.
                        SetWindowLongPtr(window, ExStyle, new IntPtr(restored & ~Layered));
                    SetWindowLongPtr(window, ExStyle, new IntPtr(restored));
                }
            _saved.Clear();
            _roots.Clear();
            _owner.Dispose();
            GC.KeepAlive(_callback);
        }
    }

    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr window, int objectId, int child, uint thread, uint time);
    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int count);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetLayeredWindowAttributes(IntPtr window, out uint color, out byte alpha, out uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetLayeredWindowAttributes(IntPtr window, uint color, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
}
