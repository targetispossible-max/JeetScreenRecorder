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
    [InlineData(1920, 1080, 60, 12000)]
    [InlineData(1920, 1080, 30, 6000)]
    [InlineData(2560, 1440, 60, 24000)]
    [InlineData(3840, 2160, 60, 45000)]
    public void RecommendedBitrate_FollowsTiers(int w, int h, int fps, int expected) =>
        Assert.Equal(expected, SizeEstimator.RecommendedBitrateKbps(w, h, fps));

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
