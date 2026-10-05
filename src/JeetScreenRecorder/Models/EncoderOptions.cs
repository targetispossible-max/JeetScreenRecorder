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
    public int AudioSampleRate { get; init; } = 48000;
    public int AudioBitrateKbps { get; init; } = 192;

    // Webcam picture-in-picture (null name = no webcam)
    public string? WebcamName { get; init; }
    public bool WebcamAutoFormat { get; init; }      // true = let the camera choose its own size / fps
    public int WebcamWidth { get; init; } = 1280;
    public int WebcamHeight { get; init; } = 720;
    public int WebcamFps { get; init; } = 30;
    public WebcamCorner WebcamCorner { get; init; } = WebcamCorner.BottomRight;
    public int WebcamPercent { get; init; } = 22;    // camera width as % of the video width
    public bool WebcamMirror { get; init; } = true;

    // Compatibility switches (used automatically when a graphics card does not support the best settings)
    public bool ForceCpuFrames { get; init; }        // never hand GPU frames straight to the encoder
    public bool BasicEncoderArgs { get; init; }      // skip optional quality switches of hardware encoders
    public string X264Preset { get; init; } = "veryfast";
}
