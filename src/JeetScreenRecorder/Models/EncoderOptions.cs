namespace JeetScreenRecorder.Models;

public enum CaptureBackend { DesktopDuplication, Gdigrab }

public sealed record EncoderOptions
{
    public CaptureBackend Backend { get; init; } = CaptureBackend.DesktopDuplication;
    public string EncoderId { get; init; } = "libx264";
    public int Fps { get; init; } = 60;
    public int MonitorIndex { get; init; }
    public int CaptureX { get; init; }          // absolute screen origin of the captured area (GDI)
    public int CaptureY { get; init; }
    public bool UseCrop { get; init; }          // region capture with DXGI
    public int CropX { get; init; }             // offset inside the monitor (DXGI)
    public int CropY { get; init; }
    public long WindowHandle { get; init; }     // != 0 => capture one application window
    public int SourceWidth { get; init; } = 1920;
    public int SourceHeight { get; init; } = 1080;
    public int OutputWidth { get; init; }       // 0 = original
    public int OutputHeight { get; init; }      // 0 = original
    public int BitrateKbps { get; init; } = 12000;
    public bool DrawMouse { get; init; } = true;
    public string? AudioPipePath { get; init; } // null = no audio
    public int AudioSampleRate { get; init; } = 48000;
    public int AudioBitrateKbps { get; init; } = 192;
}
