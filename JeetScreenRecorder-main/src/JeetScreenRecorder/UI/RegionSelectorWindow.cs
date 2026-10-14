using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using JeetScreenRecorder.Capture;
using WpfPath = System.Windows.Shapes.Path;

namespace JeetScreenRecorder.UI;

/// <summary>Full-screen dimmed overlay: drag to select, move, resize; shows size and coordinates.</summary>
public sealed class RegionSelectorWindow : Window
{
    private enum Drag { None, Create, Move, TL, TR, BL, BR }

    private readonly MonitorInfo _mon;
    private readonly RegionRect? _initial;
    private readonly Canvas _root = new() { Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), Cursor = Cursors.Cross };
    private readonly WpfPath _dim = new() { Fill = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), IsHitTestVisible = false };
    private readonly Rectangle _frame = new()
    {
        Stroke = Brushes.White, StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 4, 2 },
        IsHitTestVisible = false, Visibility = Visibility.Collapsed
    };
    private readonly Rectangle[] _handles = new Rectangle[4];
    private readonly TextBlock _label = new()
    {
        Foreground = Brushes.White, Background = new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)),
        FontSize = 14, Padding = new Thickness(8, 3, 8, 3), IsHitTestVisible = false, Visibility = Visibility.Collapsed
    };
    private ComboBox _preset = null!;

    private Rect _sel = Rect.Empty;
    private Drag _drag = Drag.None;
    private Point _start;
    private Rect _selStart;
    private bool _lockRatio;
    private double _ratio = 16.0 / 9.0;
    private bool _initDone;

    public RegionRect? Result { get; private set; }

    public RegionSelectorWindow(MonitorInfo mon, RegionRect? initial)
    {
        _mon = mon;
        _initial = initial;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;

        for (int i = 0; i < 4; i++)
        {
            _handles[i] = new Rectangle
            {
                Width = 12, Height = 12, Fill = Brushes.White, Stroke = Brushes.Black, StrokeThickness = 1,
                IsHitTestVisible = false, Visibility = Visibility.Collapsed
            };
        }
        _root.Children.Add(_dim);
        _root.Children.Add(_frame);
        foreach (var h in _handles) _root.Children.Add(h);
        _root.Children.Add(_label);

        var grid = new Grid();
        grid.Children.Add(_root);
        grid.Children.Add(BuildPanel());
        Content = grid;

        SourceInitialized += (_, _) => WindowPlacement.CoverMonitor(this, _mon);
        DpiChanged += (_, _) => WindowPlacement.CoverMonitor(this, _mon);
        ContentRendered += OnFirstRender;
        SizeChanged += (_, _) => UpdateVisuals();
        PreviewKeyDown += OnKey;
        _root.MouseLeftButtonDown += OnDown;
        _root.MouseMove += OnMove;
        _root.MouseLeftButtonUp += OnUp;
    }

    private double Scale => _mon.Width / Math.Max(1.0, _root.ActualWidth);

    private UIElement BuildPanel()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock
        {
            Text = "Drag to select  •  Enter = confirm  •  Esc = cancel",
            Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0)
        });

        _preset = new ComboBox { Width = 120, Margin = new Thickness(0, 0, 10, 0) };
        foreach (var s in new[] { "Free size", "1920×1080", "1280×720", "1024×768" }) _preset.Items.Add(s);
        _preset.SelectedIndex = 0;
        _preset.SelectionChanged += (_, _) => ApplyPreset();
        panel.Children.Add(_preset);

        var chk = new CheckBox
        {
            Content = "Lock aspect ratio", Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0)
        };
        chk.Checked += (_, _) =>
        {
            _lockRatio = true;
            if (!_sel.IsEmpty && _sel.Height > 0) _ratio = _sel.Width / _sel.Height;
        };
        chk.Unchecked += (_, _) => _lockRatio = false;
        panel.Children.Add(chk);

        var ok = new Button { Content = "✓ Confirm", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 6, 0) };
        ok.Click += (_, _) => Confirm();
        var cancel = new Button { Content = "✕ Cancel", Padding = new Thickness(12, 5, 12, 5) };
        cancel.Click += (_, _) => DialogResult = false;
        panel.Children.Add(ok);
        panel.Children.Add(cancel);

        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(230, 28, 31, 39)),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(12), Margin = new Thickness(0, 24, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            Child = panel
        };
    }

    private void OnFirstRender(object? sender, EventArgs e)
    {
        if (_initDone) return;
        _initDone = true;
        Activate();
        Focus();
        if (_initial != null)
        {
            double sc = Scale;
            _sel = new Rect(_initial.X / sc, _initial.Y / sc, _initial.Width / sc, _initial.Height / sc);
            if (_sel.Height > 0) _ratio = _sel.Width / _sel.Height;
        }
        UpdateVisuals();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; }
        else if (e.Key == Key.Enter) { Confirm(); e.Handled = true; }
    }

    private Point Clamp(Point p) =>
        new(Math.Clamp(p.X, 0, _root.ActualWidth), Math.Clamp(p.Y, 0, _root.ActualHeight));

    private Point Lock(Point anchor, Point p)
    {
        if (!_lockRatio || _ratio <= 0) return p;
        double w = Math.Abs(p.X - anchor.X);
        double h = w / _ratio;
        return new Point(anchor.X + (p.X >= anchor.X ? w : -w), anchor.Y + (p.Y >= anchor.Y ? h : -h));
    }

    private Drag HitHandle(Point p)
    {
        const double r = 14;
        if ((p - _sel.TopLeft).Length < r) return Drag.TL;
        if ((p - _sel.TopRight).Length < r) return Drag.TR;
        if ((p - _sel.BottomLeft).Length < r) return Drag.BL;
        if ((p - _sel.BottomRight).Length < r) return Drag.BR;
        return Drag.None;
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(_root);
        _start = p;
        _selStart = _sel;
        if (!_sel.IsEmpty)
        {
            _drag = HitHandle(p);
            if (_drag == Drag.None) _drag = _sel.Contains(p) ? Drag.Move : Drag.Create;
            if (_drag == Drag.Move && e.ClickCount == 2) { Confirm(); return; }
        }
        else _drag = Drag.Create;

        if (_drag == Drag.Create) _sel = new Rect(p, p);
        _root.CaptureMouse();
        UpdateVisuals();
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (_drag == Drag.None || !_root.IsMouseCaptured) return;
        var p = Clamp(e.GetPosition(_root));
        switch (_drag)
        {
            case Drag.Create:
                _sel = new Rect(_start, Clamp(Lock(_start, p)));
                break;
            case Drag.Move:
                var d = p - _start;
                double x = Math.Clamp(_selStart.X + d.X, 0, Math.Max(0, _root.ActualWidth - _selStart.Width));
                double y = Math.Clamp(_selStart.Y + d.Y, 0, Math.Max(0, _root.ActualHeight - _selStart.Height));
                _sel = new Rect(x, y, _selStart.Width, _selStart.Height);
                break;
            case Drag.TL: _sel = new Rect(Clamp(Lock(_selStart.BottomRight, p)), _selStart.BottomRight); break;
            case Drag.TR: _sel = new Rect(Clamp(Lock(_selStart.BottomLeft, p)), _selStart.BottomLeft); break;
            case Drag.BL: _sel = new Rect(Clamp(Lock(_selStart.TopRight, p)), _selStart.TopRight); break;
            case Drag.BR: _sel = new Rect(Clamp(Lock(_selStart.TopLeft, p)), _selStart.TopLeft); break;
        }
        UpdateVisuals();
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (!_root.IsMouseCaptured) return;
        _root.ReleaseMouseCapture();
        if (_drag == Drag.Create && (_sel.Width * Scale < 32 || _sel.Height * Scale < 32)) _sel = Rect.Empty;
        _drag = Drag.None;
        UpdateVisuals();
    }

    private void ApplyPreset()
    {
        if (_preset.SelectedItem is not string s || s == "Free size") return;
        var parts = s.Split('×');
        int pw = int.Parse(parts[0]), ph = int.Parse(parts[1]);
        double sc = Scale;
        double w = Math.Min(pw / sc, _root.ActualWidth), h = Math.Min(ph / sc, _root.ActualHeight);
        var c = _sel.IsEmpty
            ? new Point(_root.ActualWidth / 2, _root.ActualHeight / 2)
            : new Point(_sel.X + _sel.Width / 2, _sel.Y + _sel.Height / 2);
        double x = Math.Clamp(c.X - w / 2, 0, Math.Max(0, _root.ActualWidth - w));
        double y = Math.Clamp(c.Y - h / 2, 0, Math.Max(0, _root.ActualHeight - h));
        _sel = new Rect(x, y, w, h);
        _ratio = w / h;
        UpdateVisuals();
    }

    private void UpdateVisuals()
    {
        double w = _root.ActualWidth, h = _root.ActualHeight;
        if (w <= 0 || h <= 0) return;
        var full = new RectangleGeometry(new Rect(0, 0, w, h));

        if (_sel.IsEmpty)
        {
            _dim.Data = full;
            _frame.Visibility = Visibility.Collapsed;
            foreach (var hd in _handles) hd.Visibility = Visibility.Collapsed;
            _label.Visibility = Visibility.Collapsed;
            return;
        }

        _dim.Data = new CombinedGeometry(GeometryCombineMode.Exclude, full, new RectangleGeometry(_sel));
        _frame.Visibility = Visibility.Visible;
        Canvas.SetLeft(_frame, _sel.X);
        Canvas.SetTop(_frame, _sel.Y);
        _frame.Width = Math.Max(0, _sel.Width);
        _frame.Height = Math.Max(0, _sel.Height);

        var corners = new[] { _sel.TopLeft, _sel.TopRight, _sel.BottomLeft, _sel.BottomRight };
        for (int i = 0; i < 4; i++)
        {
            _handles[i].Visibility = Visibility.Visible;
            Canvas.SetLeft(_handles[i], corners[i].X - 6);
            Canvas.SetTop(_handles[i], corners[i].Y - 6);
        }

        double s = Scale;
        _label.Text = $"{(int)Math.Round(_sel.Width * s)} × {(int)Math.Round(_sel.Height * s)}   at ({(int)Math.Round(_sel.X * s)}, {(int)Math.Round(_sel.Y * s)})";
        _label.Visibility = Visibility.Visible;
        Canvas.SetLeft(_label, _sel.X);
        Canvas.SetTop(_label, Math.Max(0, _sel.Y - 28));
    }

    private void Confirm()
    {
        if (_sel.IsEmpty) return;
        double s = Scale;
        int x = (int)Math.Round(_sel.X * s), y = (int)Math.Round(_sel.Y * s);
        int w = (int)Math.Round(_sel.Width * s), h = (int)Math.Round(_sel.Height * s);
        x = Math.Clamp(x, 0, Math.Max(0, _mon.Width - 16));
        y = Math.Clamp(y, 0, Math.Max(0, _mon.Height - 16));
        w = Math.Min(w, _mon.Width - x) & ~1;
        h = Math.Min(h, _mon.Height - y) & ~1;
        if (w < 16 || h < 16) return;
        Result = new RegionRect(x, y, w, h);
        DialogResult = true;
    }
}

public sealed class RegionSelectorService : IRegionSelector
{
    public RegionRect? Select(MonitorInfo monitor, RegionRect? initial)
    {
        var w = new RegionSelectorWindow(monitor, initial);
        return w.ShowDialog() == true ? w.Result : null;
    }
}
