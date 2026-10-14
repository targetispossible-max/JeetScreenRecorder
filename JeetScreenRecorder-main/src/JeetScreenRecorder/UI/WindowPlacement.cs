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

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;

    public static void CoverMonitor(Window w, MonitorInfo m)
    {
        var h = new WindowInteropHelper(w).EnsureHandle();
        SetWindowPos(h, HwndTopmost, m.X, m.Y, m.Width, m.Height, SWP_NOACTIVATE);
    }

    /// <summary>Raises a topmost window above other topmost windows without taking focus.</summary>
    public static void BringToFront(Window w)
    {
        var h = new WindowInteropHelper(w).EnsureHandle();
        SetWindowPos(h, HwndTopmost, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>Position and size of the window in physical screen pixels.</summary>
    public static (int X, int Y, int W, int H) GetBounds(Window w)
    {
        var h = new WindowInteropHelper(w).EnsureHandle();
        GetWindowRect(h, out var r);
        return (r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    public static void SetBounds(Window w, int x, int y, int width, int height)
    {
        var h = new WindowInteropHelper(w).EnsureHandle();
        SetWindowPos(h, HwndTopmost, x, y, width, height, SWP_NOACTIVATE);
    }

    /// <summary>Mouse position in physical screen pixels.</summary>
    public static (int X, int Y) CursorPosition()
    {
        GetCursorPos(out var p);
        return (p.X, p.Y);
    }

    public static void MoveTo(Window w, int x, int y)
    {
        var h = new WindowInteropHelper(w).EnsureHandle();
        SetWindowPos(h, HwndTopmost, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
    }
}
