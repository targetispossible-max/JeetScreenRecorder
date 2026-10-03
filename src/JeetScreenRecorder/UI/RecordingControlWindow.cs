using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using JeetScreenRecorder.Capture;
using JeetScreenRecorder.Utils;

namespace JeetScreenRecorder.UI;

/// <summary>
/// Small floating control bar shown while recording (like Filmora): timer, pause/resume, stop and draw.
/// It is excluded from screen capture, so it never appears in the video, and it does not steal focus.
/// </summary>
public sealed class RecordingControlWindow : Window
{
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    private static readonly Brush Idle = new SolidColorBrush(Color.FromRgb(0x2A, 0x2F, 0x3A));
    private static readonly Brush StopRed = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D));
    private static readonly Brush DotRed = new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x3B));
    private static readonly Brush DotPaused = new SolidColorBrush(Color.FromRgb(0xFA, 0xCC, 0x15));
    private static readonly ControlTemplate ButtonTemplate = MakeTemplate();

    private readonly MainViewModel _vm;
    private readonly Ellipse _dot = new() { Width = 11, Height = 11, Margin = new Thickness(6, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _time = new()
    {
        Foreground = Brushes.White, FontSize = 17, FontWeight = FontWeights.SemiBold, FontFamily = new FontFamily("Consolas"),
        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), MinWidth = 66
    };
    private readonly Button _pause;
    private readonly DispatcherTimer _front = new() { Interval = TimeSpan.FromMilliseconds(400) };

    public RecordingControlWindow(MainViewModel vm)
    {
        _vm = vm;
        Title = "Recording controls";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _pause = MakeButton(_vm.PauseLabel, "Pause / resume recording (F10)", _vm.PauseResumeCommand, Idle);
        var stop = MakeButton("⏹ Stop", "Stop and save the recording (F9)", _vm.StopCommand, StopRed);
        var draw = MakeButton("✏ Draw", "Draw on the screen (F8)", _vm.AnnotateCommand, Idle);

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_dot);
        row.Children.Add(_time);
        row.Children.Add(_pause);
        row.Children.Add(stop);
        row.Children.Add(draw);

        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(240, 24, 27, 34)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x40, 0x4D)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(22),
            Padding = new Thickness(10, 6, 8, 6),
            Child = row
        };

        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            if (!SetWindowDisplayAffinity(h, WDA_EXCLUDEFROMCAPTURE))
                AppLogger.Warn("Recording bar: SetWindowDisplayAffinity failed (needs Windows 10 version 2004 or newer)");
            int ex = GetWindowLong(h, GWL_EXSTYLE);
            SetWindowLong(h, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        };
        MouseLeftButtonDown += (_, _) => { try { DragMove(); } catch { } };

        // The drawing overlay can rise above this bar; keep the bar on top so its buttons stay clickable.
        _front.Tick += (_, _) =>
        {
            if (IsVisible && Mouse.LeftButton != MouseButtonState.Pressed) WindowPlacement.BringToFront(this);
        };

        _vm.PropertyChanged += OnVmChanged;
        Closed += (_, _) => { _front.Stop(); _vm.PropertyChanged -= OnVmChanged; };
        Update();
    }

    private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.BarText) or nameof(MainViewModel.PauseLabel) or nameof(MainViewModel.IsPaused))
            Update();
    }

    private void Update()
    {
        _time.Text = _vm.BarText;
        _pause.Content = _vm.PauseLabel;
        _dot.Fill = _vm.IsPaused ? DotPaused : DotRed;
    }

    /// <summary>Shows the bar at the bottom centre of the recorded monitor (it can be dragged anywhere).</summary>
    public void ShowOnMonitor(MonitorInfo? mon)
    {
        Update();
        if (!IsVisible) Show();
        UpdateLayout();
        if (mon != null)
        {
            double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            int w = (int)(ActualWidth * scale);
            int h = (int)(ActualHeight * scale);
            WindowPlacement.MoveTo(this, mon.X + Math.Max(0, (mon.Width - w) / 2), mon.Y + mon.Height - h - 80);
        }
        WindowPlacement.BringToFront(this);
        _front.Start();
    }

    public void HideBar()
    {
        _front.Stop();
        Hide();
    }

    private static ControlTemplate MakeTemplate()
    {
        var tpl = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border)) { Name = "bd" };
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(16));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(cp);
        tpl.VisualTree = border;
        tpl.Triggers.Add(new Trigger
        {
            Property = UIElement.IsMouseOverProperty,
            Value = true,
            Setters = { new Setter(UIElement.OpacityProperty, 0.85, "bd") }
        });
        return tpl;
    }

    private static Button MakeButton(string text, string tip, ICommand command, Brush background)
    {
        var b = new Button
        {
            Content = text, ToolTip = tip, Command = command, Template = ButtonTemplate,
            Background = background, Foreground = Brushes.White, FontSize = 14, FontWeight = FontWeights.SemiBold,
            Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(3, 0, 3, 0), Cursor = Cursors.Hand
        };
        b.IsEnabledChanged += (_, _) => b.Opacity = b.IsEnabled ? 1.0 : 0.4;
        return b;
    }
}
