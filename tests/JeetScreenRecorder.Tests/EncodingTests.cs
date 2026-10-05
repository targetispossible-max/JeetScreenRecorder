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
    public void TestMode_UsesMadeUpPicture_AndNullMuxer()
    {
        var a = FfmpegArgsBuilder.Build(Opts("h264_nvenc"), null, test: true);
        Assert.Contains("testsrc2", a);
        Assert.DoesNotContain("ddagrab", a);     // encoder test must not depend on screen capture
        Assert.DoesNotContain("dshow", a);
        Assert.Contains("-frames:v 10", a);
        Assert.EndsWith("-f null -", a);
        Assert.DoesNotContain("-progress", a);
    }

    [Fact]
    public void BasicEncoderArgs_RemoveOptionalNvencSwitches()
    {
        var best = FfmpegArgsBuilder.Build(Opts("h264_nvenc"), "o.mkv");
        var basic = FfmpegArgsBuilder.Build(Opts("h264_nvenc") with { BasicEncoderArgs = true }, "o.mkv");
        Assert.Contains("-tune hq", best);
        Assert.DoesNotContain("-tune hq", basic);
        Assert.Contains("-c:v h264_nvenc", basic);
    }

    [Fact]
    public void ForceCpuFrames_DisablesZeroCopy()
    {
        var a = FfmpegArgsBuilder.Build(Opts("h264_nvenc") with { ForceCpuFrames = true }, "o.mkv");
        Assert.Contains("hwdownload", a);
        Assert.Contains("format=nv12", a);
    }

    [Fact]
    public void Colours_AreTaggedAsBt709()
    {
        var a = FfmpegArgsBuilder.Build(Opts("libx264"), "o.mkv");
        Assert.Contains("-colorspace bt709", a);
        Assert.Contains("out_color_matrix=bt709", a);
    }

    [Fact]
    public void X264_UsesChosenPreset()
    {
        var a = FfmpegArgsBuilder.Build(Opts("libx264") with { X264Preset = "superfast" }, "o.mkv");
        Assert.Contains("-preset superfast", a);
    }

    [Fact]
    public void Webcam_AddsDshowInput_AndOverlay()
    {
        var o = Opts("h264_nvenc") with { WebcamName = "Integrated Camera", WebcamCorner = WebcamCorner.BottomRight, WebcamPercent = 22 };
        var a = FfmpegArgsBuilder.Build(o, "o.mkv");
        Assert.Contains("-f dshow", a);
        Assert.Contains("-video_size 1280x720 -framerate 30", a);
        Assert.Contains("-i \"video=Integrated Camera\"", a);
        Assert.Contains("-filter_complex", a);
        Assert.Contains("overlay=x=W-w-24:y=H-h-24", a);
        Assert.Contains("-map \"[v]\"", a);
        Assert.Contains("hflip", a);
        Assert.Contains("hwdownload", a);       // webcam always uses the CPU frame path
        // both inputs come before the encoder options
        Assert.True(a.IndexOf("-i \"video=") < a.IndexOf("-c:v"));
        Assert.True(a.IndexOf("ddagrab") < a.IndexOf("-f dshow"));
    }

    [Fact]
    public void Webcam_AutoFormat_DoesNotForceSize()
    {
        var o = Opts("libx264") with { WebcamName = "Cam", WebcamAutoFormat = true };
        var a = FfmpegArgsBuilder.Build(o, "o.mkv");
        Assert.DoesNotContain("-video_size 1280x720", a);
        Assert.Contains("-i \"video=Cam\"", a);
    }

    [Theory]
    [InlineData(WebcamCorner.BottomLeft, "x=24:y=H-h-24")]
    [InlineData(WebcamCorner.TopRight, "x=W-w-24:y=24")]
    [InlineData(WebcamCorner.TopLeft, "x=24:y=24")]
    public void Webcam_CornerPositions(WebcamCorner corner, string expected)
    {
        var o = Opts("libx264") with { WebcamName = "Cam", WebcamCorner = corner };
        Assert.Contains("overlay=" + expected, FfmpegArgsBuilder.Build(o, "o.mkv"));
    }

    [Fact]
    public void Webcam_Mirror_Off_HasNoHflip()
    {
        var o = Opts("libx264") with { WebcamName = "Cam", WebcamMirror = false };
        Assert.DoesNotContain("hflip", FfmpegArgsBuilder.Build(o, "o.mkv"));
    }

    [Fact]
    public void NoWebcam_NoDshow()
    {
        Assert.DoesNotContain("dshow", FfmpegArgsBuilder.Build(Opts("libx264"), "o.mkv"));
    }

    [Fact]
    public void WebcamWidth_IsEven_AndAtMostHalf()
    {
        Assert.Equal(422, FfmpegArgsBuilder.WebcamWidthFor(1920, 22));
        Assert.Equal(0, FfmpegArgsBuilder.WebcamWidthFor(1921, 22) % 2);
        Assert.True(FfmpegArgsBuilder.WebcamWidthFor(400, 50) <= 200);
    }

    [Fact]
    public void CameraSnapshot_Args()
    {
        var a = FfmpegArgsBuilder.BuildCameraSnapshot("My Cam", "c.jpg", autoFormat: false);
        Assert.Contains("-f dshow", a);
        Assert.Contains("-i \"video=My Cam\"", a);
        Assert.Contains("-frames:v 1", a);
        Assert.DoesNotContain("-video_size", FfmpegArgsBuilder.BuildCameraSnapshot("My Cam", "c.jpg", autoFormat: true));
    }

    [Fact]
    public void ParseDevices_FindsVideoCameras_Only()
    {
        const string text = """
            [dshow @ 000001] DirectShow video devices (some may be both video and audio devices)
            [dshow @ 000001]  "Integrated Camera" (video)
            [dshow @ 000001]   Alternative name "@device_pnp_\\?\usb#vid_0c45"
            [dshow @ 000001]  "OBS Virtual Camera" (video)
            [dshow @ 000001] DirectShow audio devices
            [dshow @ 000001]  "Microphone (Realtek)" (audio)
            [dshow @ 000001]  "Integrated Camera" (video)
            """;
        var cams = Webcam.WebcamService.ParseDevices(text);
        Assert.Equal(new[] { "Integrated Camera", "OBS Virtual Camera" }, cams.Select(c => c.Name).ToArray());
    }

    [Fact]
    public void ParseDevices_NoCamera_ReturnsEmpty()
    {
        Assert.Empty(Webcam.WebcamService.ParseDevices("[dshow @ 1] Could not enumerate video devices"));
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
    public void VideoProcess_NeverContainsAudioInput()
    {
        var a = FfmpegArgsBuilder.Build(Opts("libx264"), "out.mkv");
        Assert.DoesNotContain("-f wav", a);
        Assert.DoesNotContain("tcp://", a);
        Assert.DoesNotContain("-c:a", a);
        Assert.Contains("-stats_period 0.1", a);
    }

    [Fact]
    public void Mux_WithAudio_CopiesVideo_PadsStartAndEnd()
    {
        var a = FfmpegArgsBuilder.BuildMux("v.mkv", "a.pcm", 0.4, 48000, 192, "o.mkv");
        Assert.Contains("-f s16le -ar 48000 -ac 2 -i \"a.pcm\"", a);
        Assert.Contains("adelay=400|400,apad", a);
        Assert.Contains("-c:v copy -c:a aac -b:a 192k -shortest", a);
        Assert.True(a.IndexOf("-i \"v.mkv\"") < a.IndexOf("-f s16le"));
        Assert.EndsWith("\"o.mkv\"", a);
    }

    [Fact]
    public void Mux_WithoutDelay_OnlyPadsEnd()
    {
        var a = FfmpegArgsBuilder.BuildMux("v.mkv", "a.pcm", 0, 44100, 128, "o.mkv");
        Assert.DoesNotContain("adelay", a);
        Assert.Contains("-af apad", a);
        Assert.Contains("-ar 44100", a);
    }

    [Fact]
    public void Mux_WithoutPcm_AddsSilentTrack()
    {
        var a = FfmpegArgsBuilder.BuildMux("v.mkv", null, 0, 48000, 192, "o.mkv");
        Assert.Contains("anullsrc=r=48000:cl=stereo", a);
        Assert.DoesNotContain("s16le", a);
    }

    [Fact]
    public void SegmentList_IsOrdered_AndJoinedFilesWin()
    {
        var dir = Path.Combine(Path.GetTempPath(), "jsr_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var n in new[] { "part002.mkv", "av001.mkv", "part001.mkv", "av002.mkv", "part003.mkv", "list.txt", "part003.pcm" })
                File.WriteAllBytes(Path.Combine(dir, n), new byte[] { 1 });
            var list = SegmentMuxer.ListSegments(dir);
            Assert.Equal(new[] { 1, 2, 3 }, list.Select(x => x.N).ToArray());
            Assert.Equal(new[] { true, true, false }, list.Select(x => x.Joined).ToArray());
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void SegmentInfo_RoundTrips()
    {
        var dir = Path.Combine(Path.GetTempPath(), "jsr_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            SegmentMuxer.WriteInfo(dir, 2, 0.456, 44100);
            var (d, r) = SegmentMuxer.ReadInfo(dir, 2);
            Assert.Equal(0.456, d, 3);
            Assert.Equal(44100, r);
            Assert.Equal((0.0, 48000), SegmentMuxer.ReadInfo(dir, 9));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
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
