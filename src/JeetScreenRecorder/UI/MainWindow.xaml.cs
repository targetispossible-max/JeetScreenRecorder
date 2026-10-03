using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using JeetScreenRecorder.Hotkeys;
using JeetScreenRecorder.Utils;

namespace JeetScreenRecorder.UI;

public partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);

    private const uint WDA_NONE = 0x0;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    private readonly MainViewModel _vm;
    private readonly GlobalHotkeyService _hotkeys;
    private RecordingControlWindow? _bar;
    private bool _wasActive;

    public MainWindow(MainViewModel vm, GlobalHotkeyService hotkeys)
    {
        InitializeComponent();
        Title = $"{AppInfo.Name} - {AppInfo.Build}";
        AppLogger.Info($"Running {AppInfo.Build}");
        _vm = vm;
        _hotkeys = hotkeys;
        DataContext = vm;

        SourceInitialized += (_, _) =>
        {
            ApplyCaptureAffinity();
            _hotkeys.Attach(this);
            _vm.RegisterHotkeys(_hotkeys);
        };
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.HideFromCapture)) ApplyCaptureAffinity();
            else if (e.PropertyName == nameof(MainViewModel.RecordingActive)) OnRecordingActiveChanged();
        };
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            try { _bar?.Close(); } catch { }
            _vm.Shutdown();
            _hotkeys.Dispose();
        };
    }

    /// <summary>
    /// While recording: the main window goes to the taskbar and a small control bar (timer, pause, stop, draw) shows.
    /// When recording ends the main window comes back.
    /// </summary>
    private void OnRecordingActiveChanged()
    {
        bool active = _vm.RecordingActive;
        if (active == _wasActive) return;
        _wasActive = active;
        if (active)
        {
            _bar ??= new RecordingControlWindow(_vm);
            _bar.ShowOnMonitor(_vm.SelectedMonitor);
            WindowState = WindowState.Minimized;
        }
        else
        {
            _bar?.HideBar();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        }
    }

    /// <summary>Makes this window invisible to screen capture (recording/screenshots) but visible to the user.</summary>
    private void ApplyCaptureAffinity()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        bool ok = SetWindowDisplayAffinity(hwnd, _vm.HideFromCapture ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE);
        if (!ok) AppLogger.Warn("SetWindowDisplayAffinity failed (needs Windows 10 version 2004 or newer)");
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_vm.IsBusy) return;
        e.Cancel = true;
        MessageBox.Show("A recording is in progress. Please stop the recording before closing the app.",
            AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
