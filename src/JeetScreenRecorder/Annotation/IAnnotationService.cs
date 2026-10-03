namespace JeetScreenRecorder.Annotation;

public enum AnnotationTool
{
    Pen, Highlighter, Arrow, Line, Rectangle, Circle, FilledRectangle, Text,
    NumberMarker, LaserPointer, Spotlight, Blur, Eraser
}

public interface IAnnotationService
{
    bool IsVisible { get; }
    event EventHandler? RecordToggleRequested;
    void ShowToolbar();
    void HideToolbar();
    void ToggleToolbar();
    void SetTool(AnnotationTool tool);
    void Undo();
    void Redo();
    void ClearAll();
    /// <summary>Removes every drawing and closes the annotation tools completely.</summary>
    void Exit();
    void CloseAll();
}
