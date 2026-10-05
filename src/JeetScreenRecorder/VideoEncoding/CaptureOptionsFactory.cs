using JeetScreenRecorder.Capture;
using JeetScreenRecorder.Models;

namespace JeetScreenRecorder.VideoEncoding;

/// <summary>Turns the user's capture choice (full screen / region / window) into encoder options.</summary>
public static class CaptureOptionsFactory
{
    public static bool IsValidRegion(RecordingSettings s, MonitorInfo mon) =>
        s.RegionWidth >= 16 && s.RegionHeight >= 16 &&
        s.RegionX >= 0 && s.RegionY >= 0 &&
        s.RegionX + s.RegionWidth <= mon.Width && s.RegionY + s.RegionHeight <= mon.Height;

    /// <summary>
    /// True when the camera is shown in a floating window on the screen (it is recorded together with the screen,
    /// and can be moved / resized while recording). Single-window capture cannot see other windows, so it keeps
    /// the old fixed-corner overlay done by ffmpeg.
    /// </summary>
    public static bool UsesFloatingWebcam(RecordingSettings s) =>
        s.WebcamEnabled && !string.IsNullOrWhiteSpace(s.WebcamName) && s.Source != CaptureSource.Window;

    public static EncoderOptions Create(RecordingSettings s, MonitorInfo mon)
    {
        var o = new EncoderOptions
        {
            MonitorIndex = mon.Index,
            Fps = s.Fps,
            CaptureX = mon.X,
            CaptureY = mon.Y,
            SourceWidth = mon.Width,
            SourceHeight = mon.Height,
            OutputWidth = s.Width,
            OutputHeight = s.Height,
            Backend = s.CompatibleCapture ? CaptureBackend.Gdigrab : CaptureBackend.DesktopDuplication,
            ForceCpuFrames = s.DisableZeroCopy,
            // The floating window owns the camera; ffmpeg opens it only for single-window capture.
            WebcamName = s.WebcamEnabled && !string.IsNullOrWhiteSpace(s.WebcamName) && s.Source == CaptureSource.Window ? s.WebcamName : null,
            WebcamCorner = s.WebcamPosition,
            WebcamMirror = s.WebcamMirror,
            WebcamPercent = s.WebcamSize switch { WebcamSize.Small => 15, WebcamSize.Large => 30, _ => 22 }
        };

        if (s.Source == CaptureSource.Window && s.WindowHandle != 0 && s.WindowWidth > 0 && s.WindowHeight > 0)
        {
            return o with
            {
                Backend = CaptureBackend.Gdigrab,
                WindowHandle = s.WindowHandle,
                SourceWidth = s.WindowWidth & ~1,
                SourceHeight = s.WindowHeight & ~1
            };
        }

        if (s.Source == CaptureSource.CustomRegion && IsValidRegion(s, mon))
        {
            return o with
            {
                UseCrop = true,
                CropX = s.RegionX,
                CropY = s.RegionY,
                CaptureX = mon.X + s.RegionX,
                CaptureY = mon.Y + s.RegionY,
                SourceWidth = s.RegionWidth & ~1,
                SourceHeight = s.RegionHeight & ~1
            };
        }
        return o;
    }
}
