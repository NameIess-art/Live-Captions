using System.Diagnostics;
using System.Runtime.InteropServices;
using LiveCaptions.Desktop.Capture;

namespace LiveCaptions.Desktop;

internal static class NativeSurfaceRegression
{
    // The lease is attached exclusively to THIS test process; no system-caption process is touched.
    internal static async Task VerifyAsync(Action<bool, string> check)
    {
        var root = CreateWindowEx(0, "STATIC", "LiveCaptions isolated surface regression", 0x00CF0000,
            0, 0, 320, 100, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (root == IntPtr.Zero) throw new InvalidOperationException("Cannot create isolated test HWND.");
        var original = GetWindowLongPtr(root, -20).ToInt64();
        IntPtr popup = IntPtr.Zero;
        NativeLanguageSurfaces? lease = null;
        try
        {
            lease = new NativeLanguageSurfaces(root);
            using var process = Process.GetCurrentProcess();
            check(lease.Owns((uint)process.Id), "Suppression lease belongs only to isolated test process");
            check((GetWindowLongPtr(root, -20).ToInt64() & 0x08080020) == 0x08080020,
                "Native HWND has layered, pass-through, no-activate styles");
            check(GetLayeredWindowAttributes(root, out _, out var alpha, out _) && alpha == 0, "Native HWND fully transparent");
            check(IsIconic(root), "Native root minimized without activation");
            using (var nested = new NativeLanguageSurfaces(root))
                check(nested.Owns(root), "Nested language operation shares native lease");
            check(GetLayeredWindowAttributes(root, out _, out alpha, out _) && alpha == 0, "Nested lease release keeps native hidden");
            SetLayeredWindowAttributes(root, 0, 255, 2);
            lease.Refresh();
            check(GetLayeredWindowAttributes(root, out _, out alpha, out _) && alpha == 0, "Visibility maintenance repairs alpha reset");
            popup = CreateWindowEx(0, "STATIC", "isolated popup", 0x80000000,
                0, 0, 120, 80, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            await Task.Delay(100);
            lease.Refresh();
            check(GetLayeredWindowAttributes(popup, out _, out alpha, out _) && alpha == 0, "New native process popup stays suppressed");
            lease.Dispose(); lease = null;
            check(GetWindowLongPtr(root, -20).ToInt64() == original, "Native window styles restored after final lease");
            check(!IsIconic(root), "Original native non-minimized state restored");
        }
        finally
        {
            lease?.Dispose();
            if (popup != IntPtr.Zero) DestroyWindow(popup);
            DestroyWindow(root);
        }
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateWindowExW")]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string title, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll")] private static extern bool GetLayeredWindowAttributes(IntPtr window, out uint color, out byte alpha, out uint flags);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr window, uint color, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
}
