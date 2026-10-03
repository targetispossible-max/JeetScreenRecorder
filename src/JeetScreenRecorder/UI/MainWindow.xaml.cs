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

    public MainWindow(MainViewModel vm, GlobalHotkeyService hotkeys)
    {
        InitializeComponent();
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
        };
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            _vm.Shutdown();
            _hotkeys.Dispose();
        };
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
