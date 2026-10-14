using JeetScreenRecorder.Capture;
using JeetScreenRecorder.Models;
using JeetScreenRecorder.Recording;
using JeetScreenRecorder.VideoEncoding;
using Xunit;

namespace JeetScreenRecorder.Tests;

public class CaptureTests
{
    private static readonly MonitorInfo Mon = new(0, "m", 2560, 1440, 60, true, 0, 0, "");

    [Fact]
    public void Region_BecomesCrop_WithEvenSize()
    {
        var s = new RecordingSettings
        {
            Source = CaptureSource.CustomRegion, RegionX = 100, RegionY = 50, RegionWidth = 1281, RegionHeight = 721
        };
        var o = CaptureOptionsFactory.Create(s, Mon);
        Assert.True(o.UseCrop);
        Assert.Equal(1280, o.SourceWidth);
        Assert.Equal(720, o.SourceHeight);
        Assert.Equal(100, o.CropX);
        Assert.Equal(100, o.CaptureX);
        Assert.Equal(50, o.CaptureY);

        var args = FfmpegArgsBuilder.Build(o, "out.mkv");
        Assert.Contains("video_size=1280x720:offset_x=100:offset_y=50", args);
    }

    [Fact]
    public void InvalidRegion_FallsBackToFullScreen()
    {
        var s = new RecordingSettings { Source = CaptureSource.CustomRegion, RegionWidth = 0, RegionHeight = 0 };
        var o = CaptureOptionsFactory.Create(s, Mon);
        Assert.False(o.UseCrop);
        Assert.Equal(2560, o.SourceWidth);
    }

    [Fact]
    public void RegionOutsideMonitor_IsInvalid()
    {
        var s = new RecordingSettings { RegionX = 2000, RegionY = 0, RegionWidth = 1000, RegionHeight = 500 };
        Assert.False(CaptureOptionsFactory.IsValidRegion(s, Mon));
    }

    [Fact]
    public void Window_UsesGdigrabHwnd()
    {
        var s = new RecordingSettings
        {
            Source = CaptureSource.Window, WindowHandle = 0x1234, WindowWidth = 801, WindowHeight = 600
        };
        var o = CaptureOptionsFactory.Create(s, Mon);
        Assert.Equal(CaptureBackend.Gdigrab, o.Backend);
        Assert.Equal(800, o.SourceWidth);
        var args = FfmpegArgsBuilder.Build(o, "out.mkv");
        Assert.Contains("hwnd=0x1234", args);
        Assert.DoesNotContain("ddagrab", args);
    }

    [Fact]
    public void Recovery_FindsOnlyFolders_WithRealParts()
    {
        var root = Path.Combine(Path.GetTempPath(), "jsr_" + Guid.NewGuid().ToString("N"));
        try
        {
            var good = Directory.CreateDirectory(Path.Combine(root, ".Recording_2026_parts"));
            File.WriteAllBytes(Path.Combine(good.FullName, "part001.mkv"), new byte[] { 1, 2, 3 });
            var empty = Directory.CreateDirectory(Path.Combine(root, ".Other_parts"));
            File.WriteAllBytes(Path.Combine(empty.FullName, "part001.mkv"), Array.Empty<byte>());

            var found = RecordingRecovery.FindIncomplete(root);
            Assert.Single(found);
            Assert.EndsWith(".Recording_2026_parts", found[0]);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
