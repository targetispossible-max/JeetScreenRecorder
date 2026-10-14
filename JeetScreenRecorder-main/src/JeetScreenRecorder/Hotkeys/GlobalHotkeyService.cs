using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using JeetScreenRecorder.Utils;

namespace JeetScreenRecorder.Hotkeys;

/// <summary>System-wide hotkeys (work even when the app is minimized).</summary>
public sealed class GlobalHotkeyService : IHotkeyService
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private HwndSource? _source;
    private IntPtr _hwnd;
    private readonly Dictionary<int, Action> _callbacks = new();
    private readonly Dictionary<string, int> _ids = new();
    private int _next = 1;

    public void Attach(Window window)
    {
        _hwnd = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);
    }

    public bool Register(string name, string gesture, Action callback)
    {
        if (_hwnd == IntPtr.Zero || !TryParse(gesture, out var mods, out var vk)) return false;
        Unregister(name);
        int id = _next++;
        if (!RegisterHotKey(_hwnd, id, mods | MOD_NOREPEAT, vk))
        {
            AppLogger.Warn($"Hotkey {gesture} could not be registered (already in use?)");
            return false;
        }
        _callbacks[id] = callback;
        _ids[name] = id;
        return true;
    }

    public void Unregister(string name)
    {
        if (!_ids.TryGetValue(name, out var id)) return;
        UnregisterHotKey(_hwnd, id);
        _callbacks.Remove(id);
        _ids.Remove(name);
    }

    public static bool TryParse(string gesture, out uint mods, out uint vk)
    {
        mods = 0;
        vk = 0;
        foreach (var part in gesture.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= 0x2; break;
                case "alt": mods |= 0x1; break;
                case "shift": mods |= 0x4; break;
                case "win": mods |= 0x8; break;
                default:
                    if (!Enum.TryParse<Key>(part, true, out var key)) return false;
                    vk = (uint)KeyInterop.VirtualKeyFromKey(key);
                    break;
            }
        }
        return vk != 0;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _callbacks.TryGetValue(wParam.ToInt32(), out var cb))
        {
            handled = true;
            cb();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (var name in _ids.Keys.ToList()) Unregister(name);
        _source?.RemoveHook(WndProc);
        _source = null;
    }
}
