namespace JeetScreenRecorder.Models;

public enum CaptureSource { FullScreen, Monitor, Window, CustomRegion, FixedRegion, WebcamAndScreen, ScreenWithWebcamOverlay }
public enum VideoCodec { H264, Hevc, Av1 }
public enum ContainerFormat { Mkv, Mp4, Mov }
public enum QualityPreset { Low, Medium, High, VeryHigh, Lossless }
public enum RecordingState { Idle, Recording, Paused, Finalizing }
public enum WebcamCorner { BottomRight, BottomLeft, TopRight, TopLeft }
public enum WebcamSize { Small, Medium, Large }
