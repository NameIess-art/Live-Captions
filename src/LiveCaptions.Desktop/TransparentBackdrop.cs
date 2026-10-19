using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using WinRT;
using Windows.UI;
using System.Runtime.InteropServices;

namespace LiveCaptions.Desktop;

// The compositor owns per-pixel alpha; only the caption surface changes opacity.
internal sealed class TransparentBackdrop : SystemBackdrop
{
    private Windows.UI.Composition.Compositor? _compositor;
    private Windows.UI.Composition.CompositionBrush? _brush;
    private static Windows.System.DispatcherQueueController? _systemQueue;
    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        NativeWindowStyle.ConfigureTransparentSurface(Microsoft.UI.Win32Interop.GetWindowFromWindowId(xamlRoot.ContentIslandEnvironment.AppWindowId));
        if (Windows.System.DispatcherQueue.GetForCurrentThread() == null && _systemQueue == null)
        {
            var options = new QueueOptions { Size = Marshal.SizeOf<QueueOptions>(), ThreadType = 2, ApartmentType = 2 };
            Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(options, out var pointer));
            try { _systemQueue = WinRT.MarshalInterface<Windows.System.DispatcherQueueController>.FromAbi(pointer); }
            finally { Marshal.Release(pointer); }
        }
        _compositor = new Windows.UI.Composition.Compositor();
        _brush = _compositor.CreateColorBrush(Color.FromArgb(0, 0, 0, 0));
        connectedTarget.SystemBackdrop = _brush;
    }
    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        disconnectedTarget.SystemBackdrop = null;
        _brush?.Dispose(); _brush = null;
        _compositor?.Dispose(); _compositor = null;
        base.OnTargetDisconnected(disconnectedTarget);
    }
    [StructLayout(LayoutKind.Sequential)] private struct QueueOptions { public int Size, ThreadType, ApartmentType; }
    [DllImport("CoreMessaging.dll")] private static extern int CreateDispatcherQueueController(QueueOptions options, out IntPtr controller);
}
