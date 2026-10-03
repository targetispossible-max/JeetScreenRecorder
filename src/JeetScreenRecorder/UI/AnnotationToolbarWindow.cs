using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using JeetScreenRecorder.Annotation;

namespace JeetScreenRecorder.UI;

/// <summary>Floating toolbar. It is excluded from screen capture, so it never appears in the video.</summary>
public sealed class AnnotationToolbarWindow : Window
{
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);

    private static readonly Brush Idle = new SolidColorBrush(Color.FromRgb(0x2A, 0x2F, 0x3A));
    private static readonly Brush Active = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6));
    private static readonly ControlTemplate ButtonTemplate = MakeTemplate();

    private readonly AnnotationService _svc;
    private readonly AnnotationState _st;
    private readonly Dictionary<AnnotationTool, Button> _tools = new();
    private readonly Dictionary<double, Button> _sizes = new();
    private Button _interact = null!;
    private Button _bold = null!, _italic = null!, _bg = null!;
    private Slider _opacity = null!;

    public AnnotationToolbarWindow(AnnotationService svc, AnnotationState st)
    {
        _svc = svc;
        _st = st;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowActivated = false;

        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(244, 28, 31, 39)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x40, 0x4D)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(8),
            Child = Build()
        };

        SourceInitialized += (_, _) => SetWindowDisplayAffinity(new WindowInteropHelper(this).Handle, 0x11);
        MouseLeftButtonDown += (_, _) => { try { DragMove(); } catch { } };
        RefreshSelection();
        RefreshSizes();
        RefreshInteract();
        RefreshTextToggles();
    }

    private static ControlTemplate MakeTemplate()
    {
        var tpl = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(cp);
        tpl.VisualTree = border;
        return tpl;
    }

    private static Button Btn(string text, string tip, Action onClick)
    {
        var b = new Button
        {
            Content = text, ToolTip = tip, Template = ButtonTemplate, Background = Idle, Foreground = Brushes.White,
            Padding = new Thickness(9, 5, 9, 5), Margin = new Thickness(2), FontSize = 15, MinWidth = 34, Cursor = Cursors.Hand
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    private UIElement Build()
    {
        var root = new StackPanel { Width = 660 };

        // tools
        var toolsRow = new WrapPanel();
        var tools = new (AnnotationTool Tool, string Icon, string Tip)[]
        {
            (AnnotationTool.Pen, "✏", "Pen"), (AnnotationTool.Highlighter, "🖍", "Highlighter"),
            (AnnotationTool.Arrow, "➜", "Arrow"), (AnnotationTool.Line, "╱", "Line"),
            (AnnotationTool.Rectangle, "▭", "Rectangle"), (AnnotationTool.Circle, "◯", "Circle"),
            (AnnotationTool.FilledRectangle, "■", "Filled rectangle"), (AnnotationTool.Text, "T", "Text"),
            (AnnotationTool.NumberMarker, "①", "Number marker"), (AnnotationTool.LaserPointer, "●", "Laser pointer"),
            (AnnotationTool.Spotlight, "🔦", "Spotlight"), (AnnotationTool.Blur, "▒", "Blur / hide area (mosaic)"),
            (AnnotationTool.Eraser, "⌫", "Eraser")
        };
        foreach (var (tool, icon, tip) in tools)
        {
            var b = Btn(icon, tip, () => _svc.SetTool(tool));
            _tools[tool] = b;
            toolsRow.Children.Add(b);
        }
        root.Children.Add(toolsRow);

        // colours + hex + thickness
        var colorRow = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var hex in new[] { "#EF4444", "#FACC15", "#22C55E", "#3B82F6", "#F97316", "#A855F7", "#FFFFFF", "#000000" })
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            var sw = new Button
            {
                Width = 24, Height = 24, Margin = new Thickness(3), Template = ButtonTemplate,
                Background = new SolidColorBrush(c), Cursor = Cursors.Hand, ToolTip = hex
            };
            sw.Click += (_, _) => _st.Color = c;
            colorRow.Children.Add(sw);
        }
        var hexBox = new TextBox { Width = 78, Margin = new Thickness(4, 0, 10, 0), Text = "#RRGGBB", VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "Custom colour: type #RRGGBB and press Enter" };
        hexBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            try { _st.Color = (Color)ColorConverter.ConvertFromString(hexBox.Text.Trim()); } catch { }
        };
        colorRow.Children.Add(hexBox);
        foreach (var t in new double[] { 2, 4, 6, 10, 15, 20 })
        {
            var b = Btn(t.ToString("0"), $"{t:0} px", () => { _st.Thickness = t; RefreshSizes(); });
            b.FontSize = 12;
            b.MinWidth = 28;
            _sizes[t] = b;
            colorRow.Children.Add(b);
        }
        root.Children.Add(colorRow);

        // opacity, text options, spotlight size
        var optRow = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        optRow.Children.Add(new TextBlock { Text = "Opacity", Foreground = Brushes.LightGray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 6, 0) });
        _opacity = new Slider { Minimum = 20, Maximum = 100, Value = 100, Width = 90, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        _opacity.ValueChanged += (_, _) => _st.Opacity = _opacity.Value / 100.0;
        optRow.Children.Add(_opacity);

        optRow.Children.Add(new TextBlock { Text = "Text", Foreground = Brushes.LightGray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        var sizeBox = new ComboBox { Width = 56, Margin = new Thickness(0, 0, 4, 0) };
        foreach (var s in new[] { 16, 20, 28, 36, 48, 64 }) sizeBox.Items.Add(s);
        sizeBox.SelectedItem = 28;
        sizeBox.SelectionChanged += (_, _) => { if (sizeBox.SelectedItem is int v) _st.TextSize = v; };
        optRow.Children.Add(sizeBox);
        _bold = Btn("B", "Bold", () => { _st.Bold = !_st.Bold; RefreshTextToggles(); });
        _italic = Btn("I", "Italic", () => { _st.Italic = !_st.Italic; RefreshTextToggles(); });
        _bg = Btn("BG", "Dark text background", () => { _st.TextBackground = !_st.TextBackground; RefreshTextToggles(); });
        optRow.Children.Add(_bold);
        optRow.Children.Add(_italic);
        optRow.Children.Add(_bg);

        optRow.Children.Add(new TextBlock { Text = "Spotlight", Foreground = Brushes.LightGray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 4, 0) });
        optRow.Children.Add(Btn("S", "Small spotlight", () => _st.SpotlightRadius = 100));
        optRow.Children.Add(Btn("M", "Medium spotlight", () => _st.SpotlightRadius = 170));
        optRow.Children.Add(Btn("L", "Large spotlight", () => _st.SpotlightRadius = 260));
        root.Children.Add(optRow);

        // actions
        var actRow = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        _interact = Btn("", "Switch between drawing and using your apps (clicks pass through)", () => _svc.ToggleInteractive());
        actRow.Children.Add(_interact);
        actRow.Children.Add(Btn("↶ Undo", "Undo", () => _svc.Undo()));
        actRow.Children.Add(Btn("↷ Redo", "Redo", () => _svc.Redo()));
        actRow.Children.Add(Btn("🗑 Clear all", "Remove all drawings", () => _svc.ClearAll()));
        var rec = Btn("⏺ Rec / ⏹ Stop", "Start or stop recording (F9)", () => _svc.RequestRecordToggle());
        rec.Background = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D));
        actRow.Children.Add(rec);
        actRow.Children.Add(Btn("✕ Hide toolbar", "Hide the toolbar (F8). Drawings stay visible.", () => _svc.HideToolbar()));
        root.Children.Add(actRow);

        return root;
    }

    public void RefreshSelection()
    {
        foreach (var kv in _tools) kv.Value.Background = kv.Key == _st.Tool ? Active : Idle;
        _opacity.Value = _st.Tool == AnnotationTool.Highlighter ? 40 : 100;
    }

    public void RefreshSizes()
    {
        foreach (var kv in _sizes) kv.Value.Background = Math.Abs(kv.Key - _st.Thickness) < 0.01 ? Active : Idle;
    }

    public void RefreshInteract()
    {
        _interact.Content = _st.Interactive ? "🖱 Mouse draws" : "🖱 Mouse clicks apps";
        _interact.Background = _st.Interactive ? Active : Idle;
    }

    private void RefreshTextToggles()
    {
        _bold.Background = _st.Bold ? Active : Idle;
        _italic.Background = _st.Italic ? Active : Idle;
        _bg.Background = _st.TextBackground ? Active : Idle;
    }
}
