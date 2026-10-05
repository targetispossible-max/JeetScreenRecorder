using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using JeetScreenRecorder.Capture;
using JeetScreenRecorder.Models;
using JeetScreenRecorder.Utils;
using JeetScreenRecorder.Webcam;

namespace JeetScreenRecorder.UI;

/// <summary>
/// The live camera picture as a small always-on-top window on the screen. It is NOT hidden from capture,
/// so the screen recording records it exactly as it looks. While recording it can be:
///  - moved: drag it with the left mouse button,
///  - resized: mouse wheel over it, the + / − buttons, or drag the corner handle.
/// </summary>
public sealed class WebcamOverlayWindow : Window
{
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private const int MinWidth_ = 120;
    private const double Aspect = 9.0 / 16.0;

    private readonly MainViewModel _vm;
    private readonly RecordingSettings _s;
    private readonly WebcamFeed _feed = new();
    private readonly WriteableBitmap _bmp = new(WebcamFeed.FrameWidth, WebcamFeed.FrameHeight, 96, 96, PixelFormats.Bgra32, null);
    private readonly Grid _root = new() { Background = Brushes.Black };
    private readonly StackPanel _chips = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 0, 0), Opacity = 0 };
    private readonly Border _grip;
    private readonly DispatcherTimer _front = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private int _framePending;
    private MonitorInfo? _monitor;

    private enum Drag { None, Move, Resize }
    private Drag _drag = Drag.None;
    private (int X, int Y) _dragStartCursor;
    private (int X, int Y, int W, int H) _dragStartBounds;

    public WebcamOverlayWindow(MainViewModel vm)
    {
        _vm = vm;
        _s = vm.Settings;

        Title = "Webcam";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        Background = Brushes.Black;
        Width = 360; Height = 203;

        var img = new Image { Source = _bmp, Stretch = Stretch.UniformToFill, IsHitTestVisible = false };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        _root.Children.Add(img);

        _chips.Children.Add(MakeChip("−", "Smaller", () => ResizeBy(1 / 1.15)));
        _chips.Children.Add(MakeChip("+", "Bigger", () => ResizeBy(1.15)));
        _chips.Children.Add(MakeChip("✕", "Hide the camera", HideCamera));
        _root.Children.Add(_chips);

        _grip = new Border
        {
            Width = 26, Height = 26, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), Cursor = Cursors.SizeNWSE, Opacity = 0, ToolTip = "Drag to resize",
            Child = new TextBlock { Text = "◢", Foreground = Brushes.White, FontSize = 16, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 3, 1) }
        };
        _grip.MouseLeftButtonDown += (_, e) => { BeginDrag(Drag.Resize); e.Handled = true; };
        _root.Children.Add(_grip);

        Content = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)),
            BorderThickness = new Thickness(2),
            Child = _root
        };

        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            // Tool window + no-activate: clicking the camera never takes focus away from the app being recorded.
            // (It is deliberately NOT excluded from screen capture.)
            SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        };

        _root.MouseLeftButtonDown += (_, _) => BeginDrag(Drag.Move);
        _root.MouseMove += OnDragMove;
        _root.MouseLeftButtonUp += (_, _) => EndDrag();
        _root.LostMouseCapture += (_, _) => EndDrag();
        MouseEnter += (_, _) => SetHover(true);
        MouseLeave += (_, _) => { if (_drag == Drag.None) SetHover(false); };
        MouseWheel += (_, e) => { ResizeBy(e.Delta > 0 ? 1.1 : 1 / 1.1); e.Handled = true; };

        _feed.FrameReady += OnFrame;
        _feed.Failed += msg => Dispatcher.BeginInvoke((Action)(() =>
        {
            _vm.ShowNotice(msg + " Recording continues without the camera.");
            HideOverlay();
        }));

        // Other topmost windows (drawing overlay) may rise above the camera; keep it visible.
        _front.Tick += (_, _) =>
        {
            if (IsVisible && _drag == Drag.None && Mouse.LeftButton != MouseButtonState.Pressed) WindowPlacement.BringToFront(this);
        };
        Closed += (_, _) => { _front.Stop(); _feed.Dispose(); };
    }

    // ---------------------------------------------------------------- show / hide

    /// <summary>Shows the camera on the recorded monitor and starts the live picture.</summary>
    public async void ShowOverlay(MonitorInfo? mon)
    {
        _monitor = mon;
        var cam = _s.WebcamName;
        if (string.IsNullOrWhiteSpace(cam)) return;

        Place(mon);
        if (!IsVisible) Show();
        WindowPlacement.BringToFront(this);
        _front.Start();

        var error = await _feed.StartAsync(cam, _s.WebcamMirror);
        if (error != null)
        {
            _vm.ShowNotice(error + " Recording continues without the camera.");
            HideOverlay();
        }
    }

    public void HideOverlay()
    {
        _front.Stop();
        _feed.Stop();
        Hide();
    }

    private void HideCamera() => HideOverlay();

    private void Place(MonitorInfo? mon)
    {
        int monX = mon?.X ?? 0, monY = mon?.Y ?? 0;
        int monW = mon?.Width ?? 1920, monH = mon?.Height ?? 1080;

        int w;
        if (_s.WebcamOverlayWidth >= MinWidth_) w = _s.WebcamOverlayWidth;
        else
        {
            int pct = _s.WebcamSize switch { WebcamSize.Small => 15, WebcamSize.Large => 30, _ => 22 };
            w = monW * pct / 100;
        }
        w = Math.Clamp(w, MinWidth_, Math.Max(MinWidth_, monW / 2));
        int h = (int)(w * Aspect);

        int x, y;
        bool saved = _s.WebcamOverlayPlaced
                     && _s.WebcamOverlayX >= monX && _s.WebcamOverlayY >= monY
                     && _s.WebcamOverlayX + w <= monX + monW && _s.WebcamOverlayY + h <= monY + monH;
        if (saved) { x = _s.WebcamOverlayX; y = _s.WebcamOverlayY; }
        else
        {
            int margin = Math.Max(16, monW / 80);
            int bottomExtra = 60;   // keep clear of the taskbar
            x = _s.WebcamPosition is WebcamCorner.BottomLeft or WebcamCorner.TopLeft ? monX + margin : monX + monW - w - margin;
            y = _s.WebcamPosition is WebcamCorner.TopLeft or WebcamCorner.TopRight ? monY + margin : monY + monH - h - margin - bottomExtra;
        }
        WindowPlacement.SetBounds(this, x, y, w, h);
    }

    // ---------------------------------------------------------------- live picture

    private void OnFrame()
    {
        // Skip a picture when the UI is still drawing the previous one, so the app never lags.
        if (Interlocked.Exchange(ref _framePending, 1) == 1) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, (Action)(() =>
        {
            try
            {
                lock (_feed.Sync)
                    _bmp.WritePixels(new Int32Rect(0, 0, WebcamFeed.FrameWidth, WebcamFeed.FrameHeight), _feed.Front, WebcamFeed.Stride, 0);
            }
            catch (Exception ex) { AppLogger.Warn("Webcam picture update failed: " + ex.Message); }
            finally { Interlocked.Exchange(ref _framePending, 0); }
        }));
    }

    // ---------------------------------------------------------------- move / resize

    private void BeginDrag(Drag kind)
    {
        _drag = kind;
        _dragStartCursor = WindowPlacement.CursorPosition();
        _dragStartBounds = WindowPlacement.GetBounds(this);
        _root.CaptureMouse();
    }

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (_drag == Drag.None) return;
        var c = WindowPlacement.CursorPosition();
        int dx = c.X - _dragStartCursor.X, dy = c.Y - _dragStartCursor.Y;
        var b = _dragStartBounds;
        if (_drag == Drag.Move)
        {
            WindowPlacement.SetBounds(this, b.X + dx, b.Y + dy, b.W, b.H);
        }
        else
        {
            int w = ClampWidth(b.W + Math.Max(dx, (int)(dy / Aspect)));
            WindowPlacement.SetBounds(this, b.X, b.Y, w, (int)(w * Aspect));
        }
    }

    private void EndDrag()
    {
        if (_drag == Drag.None) return;
        _drag = Drag.None;
        if (_root.IsMouseCaptured) _root.ReleaseMouseCapture();
        if (!IsMouseOver) SetHover(false);
        SaveBounds();
    }

    private int ClampWidth(int w)
    {
        int max = Math.Max(MinWidth_, (_monitor?.Width ?? 1920) * 3 / 4);
        return Math.Clamp(w, MinWidth_, max);
    }

    /// <summary>Makes the window bigger or smaller around its centre.</summary>
    private void ResizeBy(double factor)
    {
        var b = WindowPlacement.GetBounds(this);
        int w = ClampWidth((int)(b.W * factor));
        int h = (int)(w * Aspect);
        WindowPlacement.SetBounds(this, b.X + (b.W - w) / 2, b.Y + (b.H - h) / 2, w, h);
        SaveBounds();
    }

    private void SaveBounds()
    {
        var b = WindowPlacement.GetBounds(this);
        _s.WebcamOverlayPlaced = true;
        _s.WebcamOverlayX = b.X;
        _s.WebcamOverlayY = b.Y;
        _s.WebcamOverlayWidth = b.W;
        _vm.PersistSettings();
    }

    // ---------------------------------------------------------------- small helpers

    private void SetHover(bool on)
    {
        _chips.Opacity = on ? 1 : 0;
        _grip.Opacity = on ? 1 : 0;
    }

    private static Border MakeChip(string text, string tip, Action click)
    {
        var chip = new Border
        {
            Width = 28, Height = 24, Margin = new Thickness(3, 0, 3, 0), CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromArgb(190, 0, 0, 0)), Cursor = Cursors.Hand, ToolTip = tip,
            Child = new TextBlock { Text = text, Foreground = Brushes.White, FontSize = 15, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
        chip.MouseLeftButtonDown += (_, e) => e.Handled = true;       // do not start a move
        chip.MouseLeftButtonUp += (_, e) => { click(); e.Handled = true; };
        return chip;
    }
}
