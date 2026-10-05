namespace JeetScreenRecorder.Models;

public sealed class RecordingSettings
{
    // Output
    public string OutputFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Screen Recordings");
    public string FilePrefix { get; set; } = "Recording_";
    public ContainerFormat Container { get; set; } = ContainerFormat.Mkv;
    public bool AutoRemuxToMp4 { get; set; } = true;

    // Video
    public CaptureSource Source { get; set; } = CaptureSource.FullScreen;
    public int MonitorIndex { get; set; } = 0;
    public bool CompatibleCapture { get; set; } = false;   // true = GDI capture (any screen)
    public bool HideFromCapture { get; set; } = true;      // hide this app window from recordings
    public string ScreenshotFormat { get; set; } = "png";  // png | jpg | webp

    // Capture area (monitor-relative physical pixels). Width 0 = no region chosen yet.
    public int RegionX { get; set; }
    public int RegionY { get; set; }
    public int RegionWidth { get; set; }
    public int RegionHeight { get; set; }

    // Chosen at start time, never saved.
    [System.Text.Json.Serialization.JsonIgnore] public long WindowHandle { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public int WindowWidth { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public int WindowHeight { get; set; }
    public int Width { get; set; } = 0;   // 0 = original
    public int Height { get; set; } = 0;  // 0 = original
    public int Fps { get; set; } = 60;
    public QualityPreset Quality { get; set; } = QualityPreset.High;
    public VideoCodec Codec { get; set; } = VideoCodec.H264;
    public string Encoder { get; set; } = "auto";
    public int BitrateKbps { get; set; } = 0; // 0 = auto

    // Audio
    public bool MicEnabled { get; set; } = true;
    public string? MicDeviceId { get; set; }
    public double MicVolume { get; set; } = 1.0;
    public bool SystemAudioEnabled { get; set; } = true;
    public double SystemVolume { get; set; } = 0.8;
    public int AudioSampleRate { get; set; } = 48000;
    public int AudioChannels { get; set; } = 2;
    public int AudioBitrateKbps { get; set; } = 256;
    public int AudioSyncOffsetMs { get; set; } = 0;   // fine-tune: + makes audio later, - earlier (milliseconds)

    // Webcam (picture-in-picture over the screen recording)
    public bool WebcamEnabled { get; set; } = false;
    public string? WebcamName { get; set; }
    public WebcamCorner WebcamPosition { get; set; } = WebcamCorner.BottomRight;
    public WebcamSize WebcamSize { get; set; } = WebcamSize.Medium;
    public bool WebcamMirror { get; set; } = true;
    // Where the floating webcam window was last left (physical screen pixels). Placed = false -> use the corner chosen above.
    public bool WebcamOverlayPlaced { get; set; } = false;
    public int WebcamOverlayX { get; set; }
    public int WebcamOverlayY { get; set; }
    public int WebcamOverlayWidth { get; set; }   // 0 = use the Small / Medium / Large choice

    // Set automatically when GPU zero-copy encoding failed once on this PC (e.g. laptops with two graphics cards).
    public bool DisableZeroCopy { get; set; } = false;

    // General
    public bool SimpleMode { get; set; } = true;
    public int CountdownSeconds { get; set; } = 0;
    public int MinFreeDiskMb { get; set; } = 2048;

    public Dictionary<string, string> Hotkeys { get; set; } = new()
    {
        ["StartStop"] = "F9",
        ["PauseResume"] = "F10",
        ["ToggleToolbar"] = "F8",
        ["Screenshot"] = "F11",
        ["Cancel"] = "Escape"
    };
}
