using JeetScreenRecorder.Capture;
using JeetScreenRecorder.Settings;
using JeetScreenRecorder.UI;

namespace JeetScreenRecorder.Annotation;

public sealed class AnnotationService : IAnnotationService
{
    private readonly IMonitorService _monitors;
    private readonly ISettingsService _settings;
    private AnnotationOverlayWindow? _overlay;
    private AnnotationToolbarWindow? _toolbar;

    public AnnotationState State { get; } = new();
    public event EventHandler? RecordToggleRequested;

    public AnnotationService(IMonitorService monitors, ISettingsService settings)
    {
        _monitors = monitors;
        _settings = settings;
    }

    public bool IsVisible => _toolbar?.IsVisible == true;

    public void ShowToolbar()
    {
        var mon = _monitors.Get(_settings.Current.MonitorIndex);

        if (_overlay == null)
        {
            _overlay = new AnnotationOverlayWindow(State);
            // Clicking the overlay moves it above the toolbar; put the toolbar back on top so its buttons stay clickable.
            _overlay.Activated += (_, _) => RaiseToolbar();
            _overlay.ExitRequested += (_, _) => Exit();
        }
        _overlay.EffectsEnabled = true;
        _overlay.Cover(mon);
        if (!_overlay.IsVisible) _overlay.Show();

        _toolbar ??= new AnnotationToolbarWindow(this, State);
        if (!_toolbar.IsVisible) _toolbar.Show();
        _toolbar.UpdateLayout();

        double scale = System.Windows.Media.VisualTreeHelper.GetDpi(_toolbar).DpiScaleX;
        int px = (int)(_toolbar.ActualWidth * scale);
        WindowPlacement.MoveTo(_toolbar, mon.X + Math.Max(0, (mon.Width - px) / 2), mon.Y + 12);

        State.Interactive = true;
        _overlay.ApplyInteractive();
        _overlay.OnToolChanged();
        _toolbar.RefreshInteract();
    }

    public void HideToolbar()
    {
        _toolbar?.Hide();
        State.Interactive = false;
        if (_overlay != null)
        {
            _overlay.EffectsEnabled = false;
            _overlay.OnToolChanged();   // stops laser / spotlight
        }
    }

    public void ToggleToolbar()
    {
        if (IsVisible) HideToolbar(); else ShowToolbar();
    }

    public void SetTool(AnnotationTool tool)
    {
        State.Tool = tool;
        bool needsMouse = tool is not (AnnotationTool.LaserPointer or AnnotationTool.Spotlight);
        if (needsMouse) State.Interactive = true;
        _overlay?.OnToolChanged();
        _toolbar?.RefreshSelection();
        _toolbar?.RefreshInteract();
    }

    public void ToggleInteractive()
    {
        State.Interactive = !State.Interactive;
        _overlay?.ApplyInteractive();
        _toolbar?.RefreshInteract();
    }

    public void RequestRecordToggle() => RecordToggleRequested?.Invoke(this, EventArgs.Empty);

    public void Undo() => _overlay?.Undo();
    public void Redo() => _overlay?.Redo();
    public void ClearAll() => _overlay?.ClearAll();

    private void RaiseToolbar()
    {
        if (_toolbar?.IsVisible == true) WindowPlacement.BringToFront(_toolbar);
    }

    /// <summary>Clears all drawings and closes the toolbar and overlay (the screen is back to normal).</summary>
    public void Exit()
    {
        ClearAll();
        HideToolbar();
        CloseAll();
    }

    public void CloseAll()
    {
        try { _toolbar?.Close(); } catch { }
        try { _overlay?.Close(); } catch { }
        _toolbar = null;
        _overlay = null;
    }
}
