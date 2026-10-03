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
    public int AudioBitrateKbps { get; set; } = 192;

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
