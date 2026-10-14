using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using JeetScreenRecorder.Annotation;
using JeetScreenRecorder.Capture;
using WpfPath = System.Windows.Shapes.Path;

namespace JeetScreenRecorder.UI;

/// <summary>
/// Transparent always-on-top window over the recorded monitor. Everything drawn here is part of the
/// screen, so it appears in the recording, but nothing on the real desktop is modified.
/// </summary>
public sealed class AnnotationOverlayWindow : Window
{
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_TOOLWINDOW = 0x80;

    private sealed record Op(UIElement El, bool Added);

    private readonly AnnotationState _st;
    private readonly Canvas _canvas = new() { Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)) };
    private readonly List<UIElement> _committed = new();
    private readonly Stack<Op> _undo = new();
    private readonly Stack<Op> _redo = new();
    private readonly WpfPath _spot = new() { Fill = new SolidColorBrush(Color.FromArgb(185, 0, 0, 0)), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly Ellipse _laser = new() { Width = 22, Height = 22, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly DispatcherTimer _pointerTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };

    private MonitorInfo? _mon;
    private UIElement? _live;
    private Polyline? _stroke;
    private Point _p0;
    private TextBox? _editor;
    private int _counter = 1;

    /// <summary>Laser/spotlight only run while the toolbar is open.</summary>
    public bool EffectsEnabled { get; set; } = true;

    /// <summary>Raised when the user presses Esc (and no text box is being edited).</summary>
    public event EventHandler? ExitRequested;

    public AnnotationOverlayWindow(AnnotationState st)
    {
        _st = st;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        ShowActivated = false;
        Content = _canvas;

        _laser.Fill = new SolidColorBrush(Colors.Red);
        _laser.Effect = new DropShadowEffect { Color = Colors.Red, BlurRadius = 20, ShadowDepth = 0, Opacity = 1 };
        _canvas.Children.Add(_spot);
        _canvas.Children.Add(_laser);

        _canvas.MouseLeftButtonDown += OnDown;
        _canvas.MouseMove += OnMove;
        _canvas.MouseLeftButtonUp += OnUp;
        _pointerTimer.Tick += (_, _) => UpdatePointerEffects();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _editor == null)
            {
                e.Handled = true;
                ExitRequested?.Invoke(this, EventArgs.Empty);
            }
        };

        SourceInitialized += (_, _) => ApplyInteractive();
        DpiChanged += (_, _) => { if (_mon != null) WindowPlacement.CoverMonitor(this, _mon); };
    }

    public void Cover(MonitorInfo m)
    {
        _mon = m;
        WindowPlacement.CoverMonitor(this, m);
    }

    /// <summary>Toggles click-through (WS_EX_TRANSPARENT) according to the shared state.</summary>
    public void ApplyInteractive()
    {
        var h = new WindowInteropHelper(this).EnsureHandle();
        int style = GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TOOLWINDOW;
        style = _st.Interactive ? style & ~WS_EX_TRANSPARENT : style | WS_EX_TRANSPARENT;
        SetWindowLong(h, GWL_EXSTYLE, style);
        _canvas.Cursor = _st.Tool switch
        {
            AnnotationTool.Eraser => Cursors.Hand,
            AnnotationTool.Text => Cursors.IBeam,
            AnnotationTool.Pen => Cursors.Pen,
            _ => Cursors.Cross
        };
    }

    public void OnToolChanged()
    {
        CommitEditor();
        bool laser = _st.Tool == AnnotationTool.LaserPointer && EffectsEnabled;
        bool spot = _st.Tool == AnnotationTool.Spotlight && EffectsEnabled;
        if (!laser) _laser.Visibility = Visibility.Collapsed;
        if (!spot) _spot.Visibility = Visibility.Collapsed;
        if (laser || spot) _pointerTimer.Start(); else _pointerTimer.Stop();
        ApplyInteractive();
    }

    // ---------------- laser + spotlight (follow the real cursor, even in click-through mode) ----------------
    private void UpdatePointerEffects()
    {
        if (!IsVisible || !EffectsEnabled) return;
        if (_st.Tool != AnnotationTool.LaserPointer && _st.Tool != AnnotationTool.Spotlight) return;
        if (!GetCursorPos(out var cp)) return;

        Point p;
        try { p = _canvas.PointFromScreen(new Point(cp.X, cp.Y)); }
        catch { return; }

        bool inside = p.X >= 0 && p.Y >= 0 && p.X <= _canvas.ActualWidth && p.Y <= _canvas.ActualHeight;
        if (_st.Tool == AnnotationTool.LaserPointer)
        {
            _laser.Visibility = inside ? Visibility.Visible : Visibility.Collapsed;
            Canvas.SetLeft(_laser, p.X - 11);
            Canvas.SetTop(_laser, p.Y - 11);
            var c = _st.Color;
            _laser.Fill = new SolidColorBrush(c);
            _laser.Effect = new DropShadowEffect { Color = c, BlurRadius = 20, ShadowDepth = 0, Opacity = 1 };
        }
        else
        {
            if (!inside) { _spot.Visibility = Visibility.Collapsed; return; }
            var full = new RectangleGeometry(new Rect(0, 0, _canvas.ActualWidth, _canvas.ActualHeight));
            var hole = new EllipseGeometry(p, _st.SpotlightRadius, _st.SpotlightRadius);
            _spot.Data = new CombinedGeometry(GeometryCombineMode.Exclude, full, hole);
            _spot.Visibility = Visibility.Visible;
        }
    }

    // ---------------- mouse ----------------
    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (!_st.Interactive) return;
        var p = e.GetPosition(_canvas);
        CommitEditor();
        _p0 = p;

        switch (_st.Tool)
        {
            case AnnotationTool.Pen:
            case AnnotationTool.Highlighter:
                bool hl = _st.Tool == AnnotationTool.Highlighter;
                _stroke = new Polyline
                {
                    Stroke = new SolidColorBrush(_st.Color),
                    StrokeThickness = hl ? Math.Max(14, _st.Thickness * 3) : _st.Thickness,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    Opacity = _st.Opacity
                };
                _stroke.Points.Add(p);
                _canvas.Children.Add(_stroke);
                _live = _stroke;
                break;
            case AnnotationTool.Line:
            case AnnotationTool.Arrow:
            case AnnotationTool.Rectangle:
            case AnnotationTool.Circle:
            case AnnotationTool.FilledRectangle:
            case AnnotationTool.Blur:
                _live = CreateShape(p);
                _canvas.Children.Add(_live);
                break;
            case AnnotationTool.Text:
                BeginText(p);
                e.Handled = true;
                return;
            case AnnotationTool.NumberMarker:
                PlaceNumber(p);
                e.Handled = true;
                return;
            case AnnotationTool.Eraser:
                Erase(p);
                break;
        }
        _canvas.CaptureMouse();
        e.Handled = true;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!_canvas.IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(_canvas);
        switch (_st.Tool)
        {
            case AnnotationTool.Pen:
            case AnnotationTool.Highlighter:
                _stroke?.Points.Add(p);
                break;
            case AnnotationTool.Eraser:
                Erase(p);
                break;
            default:
                if (_live != null) UpdateShape(_live, _p0, p);
                break;
        }
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (!_canvas.IsMouseCaptured) return;
        _canvas.ReleaseMouseCapture();
        var p = e.GetPosition(_canvas);
        if (_live != null)
        {
            bool isStroke = _live is Polyline;
            if (isStroke)
            {
                if (_stroke != null && _stroke.Points.Count < 2) _stroke.Points.Add(new Point(p.X + 0.1, p.Y));
                Commit(_live);
            }
            else if ((p - _p0).Length < 4) _canvas.Children.Remove(_live);
            else Commit(_live);
        }
        _live = null;
        _stroke = null;
    }

    // ---------------- shapes ----------------
    private UIElement CreateShape(Point p)
    {
        var brush = new SolidColorBrush(_st.Color);
        switch (_st.Tool)
        {
            case AnnotationTool.Line:
                return new Line
                {
                    X1 = p.X, Y1 = p.Y, X2 = p.X, Y2 = p.Y, Stroke = brush, StrokeThickness = _st.Thickness,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Opacity = _st.Opacity
                };
            case AnnotationTool.Arrow:
                return new WpfPath
                {
                    Stroke = brush, Fill = brush, StrokeThickness = _st.Thickness, StrokeLineJoin = PenLineJoin.Round,
                    Opacity = _st.Opacity, Data = ArrowGeometry(p, p)
                };
            case AnnotationTool.Circle:
                return new Ellipse { Stroke = brush, StrokeThickness = _st.Thickness, Fill = Brushes.Transparent, Opacity = _st.Opacity };
            case AnnotationTool.FilledRectangle:
                return new Rectangle { Stroke = brush, Fill = brush, StrokeThickness = _st.Thickness, Opacity = _st.Opacity };
            case AnnotationTool.Blur:
                return new Rectangle { Fill = Mosaic() };
            default:
                return new Rectangle { Stroke = brush, StrokeThickness = _st.Thickness, Fill = Brushes.Transparent, Opacity = _st.Opacity };
        }
    }

    private void UpdateShape(UIElement el, Point p0, Point p1)
    {
        switch (el)
        {
            case Line l:
                l.X2 = p1.X; l.Y2 = p1.Y;
                break;
            case WpfPath arrow:
                arrow.Data = ArrowGeometry(p0, p1);
                break;
            case Shape sh:
                var r = new Rect(p0, p1);
                Canvas.SetLeft(sh, r.X);
                Canvas.SetTop(sh, r.Y);
                sh.Width = r.Width;
                sh.Height = r.Height;
                break;
        }
    }

    private Geometry ArrowGeometry(Point a, Point b)
    {
        var g = new GeometryGroup();
        g.Children.Add(new LineGeometry(a, b));
        var v = b - a;
        if (v.Length > 1)
        {
            v.Normalize();
            double head = _st.Thickness * 3 + 10;
            var n = new Vector(-v.Y, v.X);
            var baseC = b - v * head;
            var p1 = baseC + n * (head * 0.45);
            var p2 = baseC - n * (head * 0.45);
            var fig = new PathFigure { StartPoint = b, IsClosed = true, IsFilled = true };
            fig.Segments.Add(new LineSegment(p1, true));
            fig.Segments.Add(new LineSegment(p2, true));
            g.Children.Add(new PathGeometry(new[] { fig }));
        }
        return g;
    }

    /// <summary>Opaque pixel-mosaic pattern that hides what is behind it (a real blur of the desktop is not possible from a WPF overlay).</summary>
    private static Brush Mosaic()
    {
        var dark = new SolidColorBrush(Color.FromRgb(0x5B, 0x61, 0x6B));
        var light = new SolidColorBrush(Color.FromRgb(0x8A, 0x90, 0x9A));
        var dg = new DrawingGroup();
        dg.Children.Add(new GeometryDrawing(light, null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
        dg.Children.Add(new GeometryDrawing(dark, null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
        dg.Children.Add(new GeometryDrawing(dark, null, new RectangleGeometry(new Rect(8, 8, 8, 8))));
        return new DrawingBrush(dg)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 16, 16), ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, 16, 16), ViewboxUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None
        };
    }

    // ---------------- text + number markers ----------------
    private void BeginText(Point p)
    {
        _editor = new TextBox
        {
            MinWidth = 140,
            FontSize = _st.TextSize,
            FontWeight = _st.Bold ? FontWeights.Bold : FontWeights.Normal,
            FontStyle = _st.Italic ? FontStyles.Italic : FontStyles.Normal,
            Foreground = new SolidColorBrush(_st.Color),
            Background = new SolidColorBrush(Color.FromArgb(_st.TextBackground ? (byte)210 : (byte)40, _st.TextBackground ? (byte)0 : (byte)255, _st.TextBackground ? (byte)0 : (byte)255, _st.TextBackground ? (byte)0 : (byte)255)),
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4, 2, 4, 2),
            Tag = _st.TextBackground
        };
        Canvas.SetLeft(_editor, p.X);
        Canvas.SetTop(_editor, p.Y);
        _canvas.Children.Add(_editor);
        _editor.LostKeyboardFocus += (_, _) => CommitEditor();
        _editor.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) CommitEditor();
            else if (e.Key == Key.Escape) CancelEditor();
        };
        Activate();
        _editor.Focus();
        Keyboard.Focus(_editor);
    }

    private void CancelEditor()
    {
        var ed = _editor;
        _editor = null;
        if (ed != null) _canvas.Children.Remove(ed);
    }

    private void CommitEditor()
    {
        var ed = _editor;
        if (ed == null) return;
        _editor = null;
        _canvas.Children.Remove(ed);
        var text = ed.Text?.Trim() ?? "";
        if (text.Length == 0) return;

        bool bg = ed.Tag is true;
        var tb = new TextBlock
        {
            Text = text,
            FontSize = ed.FontSize,
            FontWeight = ed.FontWeight,
            FontStyle = ed.FontStyle,
            Foreground = ed.Foreground,
            Padding = new Thickness(4, 2, 4, 2),
            Opacity = _st.Opacity
        };
        if (bg) tb.Background = new SolidColorBrush(Color.FromArgb(210, 0, 0, 0));
        Canvas.SetLeft(tb, Canvas.GetLeft(ed));
        Canvas.SetTop(tb, Canvas.GetTop(ed));
        _canvas.Children.Add(tb);
        Commit(tb);
    }

    private void PlaceNumber(Point p)
    {
        double d = Math.Max(30, _st.Thickness * 4 + 14);
        var grid = new Grid { Width = d, Height = d, Opacity = _st.Opacity };
        grid.Children.Add(new Ellipse { Fill = new SolidColorBrush(_st.Color), Stroke = Brushes.White, StrokeThickness = 2 });
        grid.Children.Add(new TextBlock
        {
            Text = _counter.ToString(), Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = d * 0.5,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        });
        Canvas.SetLeft(grid, p.X - d / 2);
        Canvas.SetTop(grid, p.Y - d / 2);
        _counter++;
        _canvas.Children.Add(grid);
        Commit(grid);
    }

    // ---------------- undo / redo / erase ----------------
    private void Commit(UIElement el)
    {
        _committed.Add(el);
        _undo.Push(new Op(el, true));
        _redo.Clear();
    }

    private void Erase(Point p)
    {
        var hit = VisualTreeHelper.HitTest(_canvas, p)?.VisualHit as DependencyObject;
        while (hit != null && !ReferenceEquals(hit, _canvas))
        {
            if (hit is UIElement ue && _committed.Contains(ue))
            {
                _canvas.Children.Remove(ue);
                _committed.Remove(ue);
                _undo.Push(new Op(ue, false));
                _redo.Clear();
                return;
            }
            hit = VisualTreeHelper.GetParent(hit);
        }
    }

    public void Undo()
    {
        CommitEditor();
        if (_undo.Count == 0) return;
        var op = _undo.Pop();
        if (op.Added) { _canvas.Children.Remove(op.El); _committed.Remove(op.El); }
        else { _canvas.Children.Add(op.El); _committed.Add(op.El); }
        _redo.Push(op);
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        var op = _redo.Pop();
        if (op.Added) { _canvas.Children.Add(op.El); _committed.Add(op.El); }
        else { _canvas.Children.Remove(op.El); _committed.Remove(op.El); }
        _undo.Push(op);
    }

    public void ClearAll()
    {
        CancelEditor();
        foreach (var el in _committed.ToList()) _canvas.Children.Remove(el);
        _committed.Clear();
        _undo.Clear();
        _redo.Clear();
        _counter = 1;
    }
}
