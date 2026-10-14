using System.Runtime.InteropServices;
using System.Text;

namespace JeetScreenRecorder.Capture;

public sealed record WindowInfo(long Handle, string Title, int Width, int Height)
{
    public string Display => $"{Title}  ({Width}×{Height})";
}

public interface IWindowService
{
    IReadOnlyList<WindowInfo> GetWindows();
    (int Width, int Height)? GetSize(long handle);
}

public sealed class WindowService : IWindowService
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    public IReadOnlyList<WindowInfo> GetWindows()
    {
        var list = new List<WindowInfo>();
        uint self = (uint)Environment.ProcessId;

        bool Callback(IntPtr h, IntPtr l)
        {
            if (!IsWindowVisible(h) || IsIconic(h)) return true;
            if ((GetWindowLong(h, -20) & 0x80) != 0) return true;          // tool windows
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == self) return true;                                   // our own windows
            DwmGetWindowAttribute(h, 14, out int cloaked, sizeof(int));     // hidden UWP ghosts
            if (cloaked != 0) return true;
            int len = GetWindowTextLength(h);
            if (len == 0) return true;
            var sb = new StringBuilder(len + 1);
            GetWindowText(h, sb, sb.Capacity);
            var title = sb.ToString().Trim();
            if (title.Length == 0 || title == "Program Manager") return true;
            if (!GetWindowRect(h, out var r)) return true;
            int w = r.Right - r.Left, ht = r.Bottom - r.Top;
            if (w < 100 || ht < 60) return true;
            list.Add(new WindowInfo(h.ToInt64(), title, w, ht));
            return true;
        }

        EnumWindows(Callback, IntPtr.Zero);
        return list.OrderBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public (int Width, int Height)? GetSize(long handle)
    {
        var h = new IntPtr(handle);
        if (!IsWindow(h) || IsIconic(h) || !GetWindowRect(h, out var r)) return null;
        int w = r.Right - r.Left, ht = r.Bottom - r.Top;
        return w > 0 && ht > 0 ? (w, ht) : null;
    }
}
