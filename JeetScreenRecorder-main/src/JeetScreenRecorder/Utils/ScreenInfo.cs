using System.Runtime.InteropServices;

namespace JeetScreenRecorder.Utils;

public static class ScreenInfo
{
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    /// <summary>Primary monitor size in physical pixels (app is per-monitor DPI aware).</summary>
    public static (int Width, int Height) PrimarySize()
    {
        int w = GetSystemMetrics(0), h = GetSystemMetrics(1);
        return (w > 0 ? w : 1920, h > 0 ? h : 1080);
    }
}
