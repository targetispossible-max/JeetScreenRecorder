using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using JeetScreenRecorder.Capture;

namespace JeetScreenRecorder.UI;

/// <summary>Places WPF windows using physical pixels, so it works on mixed-DPI multi-monitor setups.</summary>
public static class WindowPlacement
{
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;

    public static void CoverMonitor(Window w, MonitorInfo m)
    {
        var h = new WindowInteropHelper(w).EnsureHandle();
        SetWindowPos(h, HwndTopmost, m.X, m.Y, m.Width, m.Height, SWP_NOACTIVATE);
    }

    public static void MoveTo(Window w, int x, int y)
    {
        var h = new WindowInteropHelper(w).EnsureHandle();
        SetWindowPos(h, HwndTopmost, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
    }
}
