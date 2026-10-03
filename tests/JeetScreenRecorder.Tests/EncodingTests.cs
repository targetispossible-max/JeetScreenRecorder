using JeetScreenRecorder.Models;
using JeetScreenRecorder.Recording;
using JeetScreenRecorder.VideoEncoding;
using Xunit;

namespace JeetScreenRecorder.Tests;

public class EncodingTests
{
    private static EncoderOptions Opts(string enc, int ow = 0, int oh = 0,
        CaptureBackend b = CaptureBackend.DesktopDuplication) => new()
    {
        EncoderId = enc, OutputWidth = ow, OutputHeight = oh,
        SourceWidth = 1920, SourceHeight = 1080, Fps = 60, BitrateKbps = 12000, Backend = b
    };

    [Fact]
    public void Nvenc_WithoutScale_IsZeroCopy()
    {
        var a = FfmpegArgsBuilder.Build(Opts("h264_nvenc"), "out.mkv");
        Assert.Contains("ddagrab", a);
        Assert.DoesNotContain("hwdownload", a);
        Assert.Contains("-c:v h264_nvenc", a);
        Assert.Contains("-b:v 12000k", a);
    }

    [Fact]
    public void Nvenc_WithScale_UsesCpuScale()
    {
        var a = FfmpegArgsBuilder.Build(Opts("h264_nvenc", 1280, 720), "out.mkv");
        Assert.Contains("hwdownload", a);
        Assert.Contains("scale=1280:720", a);
    }

    [Fact]
    public void LargerThanSource_IsNeverUpscaled()
    {
        var o = Opts("h264_nvenc", 3840, 2160);
        Assert.False(FfmpegArgsBuilder.NeedsScale(o));
        Assert.Equal((1920, 1080), FfmpegArgsBuilder.OutputSize(o));
    }

    [Fact]
    public void Software_Fallback_UsesGdigrabAndX264()
    {
        var a = FfmpegArgsBuilder.Build(Opts("libx264", 0, 0, CaptureBackend.Gdigrab), "out.mkv");
        Assert.Contains("gdigrab", a);
        Assert.Contains("libx264", a);
        Assert.DoesNotContain("ddagrab", a);
    }

    [Fact]
    public void TestMode_WritesToNullMuxer()
    {
        var a = FfmpegArgsBuilder.Build(Opts("h264_nvenc"), null, test: true);
        Assert.Contains("-frames:v 5", a);
        Assert.EndsWith("-f null -", a);
        Assert.DoesNotContain("-progress", a);
    }

    [Fact]
    public void ConcatList_EscapesQuotes_AndUsesForwardSlashes()
    {
        var list = ConcatList.Build(new[] { @"C:\a\it's.mkv" });
        Assert.Equal("file 'C:/a/it'\\''s.mkv'\n", list);
    }

    [Fact]
    public void Selector_PrefersNvenc_ThenFallsBack()
    {
        var all = new List<EncoderInfo>
        {
            new("h264_qsv", "Intel", VideoCodec.H264, true),
            new("h264_nvenc", "NVIDIA", VideoCodec.H264, true),
            EncoderSelector.Software
        };
        Assert.Equal("h264_nvenc", EncoderSelector.Choose(all, VideoCodec.H264, "auto").Id);
        Assert.Equal("h264_qsv", EncoderSelector.Choose(all, VideoCodec.H264, "h264_qsv").Id);
        Assert.Equal("libx264", EncoderSelector.Choose(new List<EncoderInfo>(), VideoCodec.Hevc, "auto").Id);
    }

    [Fact]
    public void Audio_InputComesBeforeFilters_AndAacIsUsed()
    {
        var o = Opts("libx264") with { AudioPipePath = @"\\.\pipe\x", AudioSampleRate = 48000, AudioBitrateKbps = 192 };
        var a = FfmpegArgsBuilder.Build(o, "out.mkv");
        Assert.Contains("-f s16le -ar 48000 -ac 2", a);
        Assert.Contains("-c:a aac -b:a 192k", a);
        Assert.True(a.IndexOf("-f s16le") < a.IndexOf("-vf"));
    }

    [Fact]
    public void NoAudioPipe_MeansNoAudioArgs()
    {
        var a = FfmpegArgsBuilder.Build(Opts("h264_nvenc"), "out.mkv");
        Assert.DoesNotContain("s16le", a);
        Assert.DoesNotContain("-c:a", a);
    }

    [Fact]
    public void Gdigrab_UsesSelectedMonitorGeometry()
    {
        var o = Opts("libx264", 0, 0, CaptureBackend.Gdigrab) with { CaptureX = -1920, CaptureY = 0 };
        var a = FfmpegArgsBuilder.Build(o, "out.mkv");
        Assert.Contains("-offset_x -1920 -offset_y 0 -video_size 1920x1080", a);
    }

    [Fact]
    public void Ddagrab_UsesSelectedMonitorIndex()
    {
        var o = Opts("h264_nvenc") with { MonitorIndex = 1 };
        Assert.Contains("output_idx=1", FfmpegArgsBuilder.Build(o, "out.mkv"));
    }

    [Fact]
    public void Screenshot_Args_CaptureOneFrame()
    {
        var a = FfmpegArgsBuilder.BuildScreenshot(Opts("libx264"), "s.png");
        Assert.Contains("-frames:v 1", a);
        Assert.Contains("ddagrab", a);
        Assert.EndsWith("\"s.png\"", a);
    }
}
