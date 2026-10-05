using JeetScreenRecorder.Models;
using JeetScreenRecorder.Recording;
using JeetScreenRecorder.Settings;
using JeetScreenRecorder.Utils;
using Xunit;

namespace JeetScreenRecorder.Tests;

public class CoreTests
{
    [Fact]
    public void FileName_MatchesSpecExample()
    {
        var name = OutputNaming.Generate("YouTube_", "mp4", new DateTime(2026, 10, 3, 8, 30, 15));
        Assert.Equal("YouTube_2026-10-03_08-30-15.mp4", name);
    }

    [Fact]
    public void SizeEstimate_1080p60_IsAbout7Gb()
    {
        var gb = SizeEstimator.GbPerHour(16000, 192);
        Assert.InRange(gb, 7.2, 7.4);
    }

    [Theory]
    [InlineData(1920, 1080, 60, 16000)]
    [InlineData(1920, 1080, 30, 8000)]
    [InlineData(1280, 720, 60, 8000)]
    [InlineData(2560, 1440, 60, 30000)]
    [InlineData(3840, 2160, 60, 60000)]
    public void RecommendedBitrate_FollowsTiers(int w, int h, int fps, int expected) =>
        Assert.Equal(expected, SizeEstimator.RecommendedBitrateKbps(w, h, fps));

    [Fact]
    public void Quality_Presets_AreOrdered()
    {
        var m = new[] { QualityPreset.Low, QualityPreset.Medium, QualityPreset.High, QualityPreset.VeryHigh, QualityPreset.Lossless }
            .Select(VideoEncoding.FfmpegArgsBuilder.QualityMultiplier).ToArray();
        Assert.Equal(m.OrderBy(x => x).ToArray(), m);
        Assert.Equal(1.0, VideoEncoding.FfmpegArgsBuilder.QualityMultiplier(QualityPreset.High));
    }

    [Fact]
    public void Settings_Webcam_RoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"psr_{Guid.NewGuid():N}.json");
        try
        {
            var a = new JsonSettingsService(path);
            a.Current.WebcamEnabled = true;
            a.Current.WebcamName = "Integrated Camera";
            a.Current.WebcamPosition = WebcamCorner.TopLeft;
            a.Current.WebcamSize = WebcamSize.Large;
            a.Save();

            var b = new JsonSettingsService(path).Current;
            Assert.True(b.WebcamEnabled);
            Assert.Equal("Integrated Camera", b.WebcamName);
            Assert.Equal(WebcamCorner.TopLeft, b.WebcamPosition);
            Assert.Equal(WebcamSize.Large, b.WebcamSize);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void CaptureOptions_CarryWebcamSettings()
    {
        var s = new RecordingSettings { WebcamEnabled = true, WebcamName = "Cam", WebcamSize = WebcamSize.Small, WebcamPosition = WebcamCorner.TopRight };
        var mon = new Capture.MonitorInfo(0, "m", 1920, 1080, 60, true, 0, 0, "");
        // Full screen / region: the floating webcam window shows the camera, ffmpeg must NOT open it a second time.
        Assert.True(VideoEncoding.CaptureOptionsFactory.UsesFloatingWebcam(s));
        var o = VideoEncoding.CaptureOptionsFactory.Create(s, mon);
        Assert.Null(o.WebcamName);
        Assert.Equal(15, o.WebcamPercent);
        Assert.Equal(WebcamCorner.TopRight, o.WebcamCorner);

        // Single-window capture cannot see the floating window, so ffmpeg overlays the camera itself.
        s.Source = CaptureSource.Window;
        s.WindowHandle = 0x1234; s.WindowWidth = 800; s.WindowHeight = 600;
        Assert.False(VideoEncoding.CaptureOptionsFactory.UsesFloatingWebcam(s));
        Assert.Equal("Cam", VideoEncoding.CaptureOptionsFactory.Create(s, mon).WebcamName);

        s.WebcamEnabled = false;
        Assert.Null(VideoEncoding.CaptureOptionsFactory.Create(s, mon).WebcamName);
    }

    [Fact]
    public void WebcamOverlay_Settings_RoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"psr_{Guid.NewGuid():N}.json");
        try
        {
            var a = new JsonSettingsService(path);
            a.Current.WebcamOverlayPlaced = true;
            a.Current.WebcamOverlayX = 321;
            a.Current.WebcamOverlayY = 123;
            a.Current.WebcamOverlayWidth = 480;
            a.Save();

            var b = new JsonSettingsService(path).Current;
            Assert.True(b.WebcamOverlayPlaced);
            Assert.Equal(321, b.WebcamOverlayX);
            Assert.Equal(123, b.WebcamOverlayY);
            Assert.Equal(480, b.WebcamOverlayWidth);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Settings_RoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"psr_{Guid.NewGuid():N}.json");
        try
        {
            var a = new JsonSettingsService(path);
            a.Current.Fps = 30;
            a.Current.Codec = VideoCodec.Hevc;
            a.Save();

            var b = new JsonSettingsService(path);
            Assert.Equal(30, b.Current.Fps);
            Assert.Equal(VideoCodec.Hevc, b.Current.Codec);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Settings_CorruptFile_FallsBackToDefaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"psr_{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ not json");
        try { Assert.Equal(60, new JsonSettingsService(path).Current.Fps); }
        finally { File.Delete(path); }
    }
}
