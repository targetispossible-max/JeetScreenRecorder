using System.Runtime.InteropServices;
using JeetScreenRecorder.Utils;

namespace JeetScreenRecorder.Capture;

public sealed class MonitorService : IMonitorService
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion; public ushort dmDriverVersion; public ushort dmSize; public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX; public int dmPositionY;
        public uint dmDisplayOrientation; public uint dmDisplayFixedOutput;
        public short dmColor; public short dmDuplex; public short dmYResolution; public short dmTTOption; public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel; public uint dmPelsWidth; public uint dmPelsHeight; public uint dmDisplayFlags; public uint dmDisplayFrequency;
        public uint dmICMMethod; public uint dmICMIntent; public uint dmMediaType; public uint dmDitherType;
        public uint dmReserved1; public uint dmReserved2; public uint dmPanningWidth; public uint dmPanningHeight;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DEVMODE devMode);

    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var raw = new List<(RECT Rect, bool Primary, string Device)>();
        bool Callback(IntPtr hMon, IntPtr hdc, ref RECT r, IntPtr data)
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(hMon, ref mi)) raw.Add((mi.rcMonitor, (mi.dwFlags & 1) != 0, mi.szDevice));
            return true;
        }

        try { EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Callback, IntPtr.Zero); }
        catch (Exception ex) { AppLogger.Error("Monitor enumeration failed", ex); }

        var sorted = raw.OrderByDescending(m => m.Primary).ThenBy(m => m.Rect.Left).ThenBy(m => m.Rect.Top).ToList();
        var list = new List<MonitorInfo>();
        for (int i = 0; i < sorted.Count; i++)
        {
            var m = sorted[i];
            int w = m.Rect.Right - m.Rect.Left, h = m.Rect.Bottom - m.Rect.Top;
            int hz = RefreshRate(m.Device);
            var name = $"Monitor {i + 1}{(m.Primary ? " (Primary)" : "")} – {w}×{h} @ {hz} Hz";
            list.Add(new MonitorInfo(i, name, w, h, hz, m.Primary, m.Rect.Left, m.Rect.Top, m.Device));
        }

        if (list.Count == 0)
        {
            var (w, h) = ScreenInfo.PrimarySize();
            list.Add(new MonitorInfo(0, $"Monitor 1 (Primary) – {w}×{h}", w, h, 60, true, 0, 0, ""));
        }
        AppLogger.Info("Monitors: " + string.Join(" | ", list.Select(l => $"{l.Index}:{l.Width}x{l.Height}@{l.X},{l.Y}")));
        return list;
    }

    public MonitorInfo Get(int index)
    {
        var list = GetMonitors();
        return list.FirstOrDefault(m => m.Index == index) ?? list.FirstOrDefault(m => m.IsPrimary) ?? list[0];
    }

    private static int RefreshRate(string device)
    {
        try
        {
            var dm = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
            if (EnumDisplaySettings(device, -1, ref dm) && dm.dmDisplayFrequency > 1) return (int)dm.dmDisplayFrequency;
        }
        catch { }
        return 60;
    }
}
