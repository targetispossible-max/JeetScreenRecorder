using System.Windows.Media;

namespace JeetScreenRecorder.Annotation;

/// <summary>Shared drawing options used by the overlay and the toolbar.</summary>
public sealed class AnnotationState
{
    public AnnotationTool Tool { get; set; } = AnnotationTool.Pen;
    public Color Color { get; set; } = Colors.Red;
    public double Thickness { get; set; } = 4;
    public double Opacity { get; set; } = 1.0;
    public double TextSize { get; set; } = 28;
    public bool Bold { get; set; } = true;
    public bool Italic { get; set; }
    public bool TextBackground { get; set; }
    public double SpotlightRadius { get; set; } = 170;
    /// <summary>true = the overlay receives the mouse (drawing); false = clicks pass through to apps.</summary>
    public bool Interactive { get; set; } = true;
}
