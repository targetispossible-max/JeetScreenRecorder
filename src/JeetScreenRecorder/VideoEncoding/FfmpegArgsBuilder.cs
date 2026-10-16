using System.Text;
using JeetScreenRecorder.Models;

namespace JeetScreenRecorder.VideoEncoding;

public static class FfmpegArgsBuilder
{
    public static bool IsNvenc(string id) => id.EndsWith("_nvenc", StringComparison.Ordinal);
    private static bool IsQsv(string id) => id.EndsWith("_qsv", StringComparison.Ordinal);
    private static bool IsAmf(string id) => id.EndsWith("_amf", StringComparison.Ordinal);
    public static bool IsHardware(string id) => IsNvenc(id) || IsQsv(id) || IsAmf(id);

    public static double QualityMultiplier(QualityPreset q) => q switch
    {
        QualityPreset.Low => 0.5,
        QualityPreset.Medium => 0.75,
        QualityPreset.High => 1.0,
        QualityPreset.VeryHigh => 1.5,
        QualityPreset.Lossless => 2.2, // "Ultra": near-lossless (very high bitrate)
        _ => 1.0
    };

    /// <summary>Constant-quality value per quality preset (OBS guides use 16-23 for good local recordings).</summary>
    public static int CqFor(QualityPreset q) => q switch
    {
        QualityPreset.Low => 28,
        QualityPreset.Medium => 24,
        QualityPreset.High => 21,
        QualityPreset.VeryHigh => 18,
        QualityPreset.Lossless => 14,
        _ => 21
    };

    /// <summary>Scale only when the target is smaller than the source (never upscale).</summary>
    public static bool NeedsScale(EncoderOptions o) =>
        o.OutputWidth > 0 && o.OutputHeight > 0 &&
        o.OutputWidth <= o.SourceWidth && o.OutputHeight <= o.SourceHeight &&
        (o.OutputWidth < o.SourceWidth || o.OutputHeight < o.SourceHeight);

    public static (int Width, int Height) OutputSize(EncoderOptions o) =>
        NeedsScale(o) ? (o.OutputWidth, o.OutputHeight) : (o.SourceWidth & ~1, o.SourceHeight & ~1);

    public static bool HasWebcam(EncoderOptions o) => !string.IsNullOrWhiteSpace(o.WebcamName);
    public static bool HasWatermark(EncoderOptions o) => !string.IsNullOrWhiteSpace(o.WatermarkPath);

    /// <summary>Watermark width: a medium-size mark, about 20 % of the video width (even number, at least 120 px).</summary>
    public static int WatermarkWidthFor(int videoWidth) => Math.Max(120, (int)(videoWidth * 0.20)) & ~1;

    private static void AppendVideoInput(StringBuilder sb, EncoderOptions o, int fps, bool mouse)
    {
        int m = mouse ? 1 : 0;
        if (o.WindowHandle != 0)
        {
            sb.Append($"-f gdigrab -framerate {fps} -draw_mouse {m} -i hwnd=0x{o.WindowHandle:X} ");
        }
        else if (o.Backend == CaptureBackend.DesktopDuplication)
        {
            var crop = o.UseCrop
                ? $":video_size={o.SourceWidth}x{o.SourceHeight}:offset_x={o.CropX}:offset_y={o.CropY}"
                : "";
            sb.Append($"-f lavfi -i \"ddagrab=output_idx={o.MonitorIndex}:framerate={fps}:draw_mouse={m}{crop}\" ");
        }
        else
        {
            sb.Append($"-f gdigrab -framerate {fps} -draw_mouse {m} -offset_x {o.CaptureX} -offset_y {o.CaptureY} " +
                      $"-video_size {o.SourceWidth}x{o.SourceHeight} -i desktop ");
        }
    }

    private static void AppendWebcamInput(StringBuilder sb, EncoderOptions o)
    {
        var name = (o.WebcamName ?? "").Replace("\"", "");
        sb.Append("-f dshow -rtbufsize 256M -thread_queue_size 512 ");
        if (!o.WebcamAutoFormat)
            sb.Append($"-video_size {o.WebcamWidth}x{o.WebcamHeight} -framerate {o.WebcamFps} ");
        sb.Append($"-i \"video={name}\" ");
    }

    public static string Build(EncoderOptions o, string? outputPath, bool test = false)
    {
        var sb = new StringBuilder("-hide_banner -y -loglevel error ");

        if (test)
        {
            // Encoder self-test with a made-up picture: independent of the display and of the camera,
            // so a hardware encoder is never rejected only because screen capture is unavailable.
            sb.Append("-f lavfi -i \"testsrc2=size=1280x720:rate=30\" ");
            sb.Append($"-vf \"format={(IsHardware(o.EncoderId) ? "nv12" : "yuv420p")}\" ");
            AppendEncoder(sb, o);
            sb.Append("-frames:v 10 -f null -");
            return sb.ToString();
        }

        sb.Append("-progress pipe:1 -nostats -stats_period 0.1 ");
        bool gpuFrames = o.Backend == CaptureBackend.DesktopDuplication && o.WindowHandle == 0;
        bool cam = HasWebcam(o);

        // ---- inputs (all inputs must come before any output option) ----
        AppendVideoInput(sb, o, o.Fps, o.DrawMouse);
        if (cam) AppendWebcamInput(sb, o);
        bool wm = HasWatermark(o);
        if (wm) sb.Append($"-i \"{o.WatermarkPath!.Replace("\"", "")}\" ");

        // ---- video filters ----
        if (cam || wm)
        {
            sb.Append($"-filter_complex \"{OverlayGraph(o, gpuFrames, cam, wm)}\" -map \"[v]\" ");
        }
        else
        {
            // NVENC can take GPU frames directly (zero-copy) when no scaling is needed.
            bool cpuPath = !gpuFrames || !IsNvenc(o.EncoderId) || NeedsScale(o) || o.ForceCpuFrames;
            if (cpuPath) sb.Append($"-vf \"{CpuFilter(o, gpuFrames)}\" ");
        }

        AppendEncoder(sb, o);
        sb.Append($"-g {o.Fps * 2} -colorspace bt709 -color_primaries bt709 -color_trc bt709 ");
        sb.Append($"\"{outputPath}\"");
        return sb.ToString();
    }

    /// <summary>
    /// Joins one video-only segment with its raw PCM audio (s16le stereo) into one A/V segment. The video is copied (no re-encode).
    /// <paramref name="audioDelaySeconds"/> pads silence at the start so audio and video line up; <c>apad</c> + <c>-shortest</c>
    /// make the audio exactly as long as the video. <paramref name="videoStretch"/> &gt; 1 stretches the video timeline
    /// (real recording time / video length). Pass pcmPath = null to add a silent track instead
    /// (so every segment has the same streams and can be joined later).
    /// </summary>
    public static string BuildMux(string videoPath, string? pcmPath, double audioDelaySeconds,
        int sampleRate, int audioKbps, string outputPath, double videoStretch = 1.0)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var sb = new StringBuilder("-hide_banner -y -loglevel error ");
        sb.Append($"-i \"{videoPath}\" ");
        if (pcmPath != null)
        {
            sb.Append($"-f s16le -ar {sampleRate} -ac 2 -i \"{pcmPath}\" ");
            int ms = (int)Math.Round(Math.Max(0, audioDelaySeconds) * 1000.0);
            sb.Append(ms > 0 ? $"-af \"adelay={ms.ToString(inv)}|{ms.ToString(inv)},apad\" " : "-af apad ");
        }
        else
        {
            sb.Append($"-f lavfi -i \"anullsrc=r={sampleRate}:cl=stereo\" ");
        }
        sb.Append($"-map 0:v:0 -map 1:a:0 -c:v copy -c:a aac -b:a {audioKbps}k -shortest ");
        // Video timestamps were "compressed" by a capture that could not keep real-time speed: stretch them back
        // to real time (no re-encode) so the video is as long as the time that was really recorded.
        if (videoStretch > 1.0005)
        {
            var r = videoStretch.ToString("0.######", inv);
            sb.Append($"-bsf:v \"setts=pts=PTS*{r}:dts=DTS*{r}\" ");
        }
        sb.Append($"\"{outputPath}\"");
        return sb.ToString();
    }

    public static string BuildScreenshot(EncoderOptions o, string outputPath)
    {
        var sb = new StringBuilder("-hide_banner -y -loglevel error ");
        AppendVideoInput(sb, o, 1, mouse: false);
        sb.Append("-frames:v 1 ");
        if (o.Backend == CaptureBackend.DesktopDuplication && o.WindowHandle == 0) sb.Append("-vf \"hwdownload,format=bgra\" ");
        sb.Append($"\"{outputPath}\"");
        return sb.ToString();
    }

    /// <summary>One frame from a camera, used by the "Test camera" button.</summary>
    public static string BuildCameraSnapshot(string cameraName, string outputPath, bool autoFormat)
    {
        var name = cameraName.Replace("\"", "");
        var sb = new StringBuilder("-hide_banner -y -loglevel error -f dshow -rtbufsize 64M ");
        if (!autoFormat) sb.Append("-video_size 1280x720 -framerate 30 ");
        sb.Append($"-i \"video={name}\" -frames:v 1 -update 1 \"{outputPath}\"");
        return sb.ToString();
    }

    /// <summary>Camera width in pixels for the given video width (even number, never more than half the video).</summary>
    public static int WebcamWidthFor(int videoWidth, int percent)
    {
        int w = (int)(videoWidth * Math.Clamp(percent, 5, 50) / 100.0);
        w = Math.Min(w, videoWidth / 2);
        return Math.Max(64, w) & ~1;
    }

    private static string OverlayGraph(EncoderOptions o, bool fromGpuFrames, bool cam, bool wm)
    {
        var (w, _) = OutputSize(o);
        int margin = Math.Max(12, w / 80);

        var bg = new List<string>();
        if (fromGpuFrames) { bg.Add("hwdownload"); bg.Add("format=bgra"); }
        bg.Add(NeedsScale(o)
            ? $"scale={o.OutputWidth}:{o.OutputHeight}:flags=lanczos:out_color_matrix=bt709"
            : "scale=trunc(iw/2)*2:trunc(ih/2)*2:out_color_matrix=bt709");
        bg.Add("format=yuv420p");

        string last = IsHardware(o.EncoderId) ? "nv12" : "yuv420p";
        var g = new StringBuilder($"[0:v]{string.Join(",", bg)}[bg]");
        string cur = "bg";

        if (cam)
        {
            int camW = WebcamWidthFor(w, o.WebcamPercent);
            var c = new List<string>();
            if (o.WebcamMirror) c.Add("hflip");
            c.Add($"scale={camW}:-2:flags=bicubic");
            c.Add("format=yuv420p");
            string pos = o.WebcamCorner switch
            {
                WebcamCorner.BottomLeft => $"x={margin}:y=H-h-{margin}",
                WebcamCorner.TopRight => $"x=W-w-{margin}:y={margin}",
                WebcamCorner.TopLeft => $"x={margin}:y={margin}",
                _ => $"x=W-w-{margin}:y=H-h-{margin}"
            };
            g.Append($";[1:v]{string.Join(",", c)}[cam];[bg][cam]overlay={pos}");
            if (wm) { g.Append("[withcam]"); cur = "withcam"; }
            else { g.Append($",format={last}[v]"); return g.ToString(); }
        }

        // Watermark: always the bottom-right corner, on top of everything (also on top of the camera picture).
        int wmIndex = cam ? 2 : 1;
        g.Append($";[{wmIndex}:v]scale={WatermarkWidthFor(w)}:-2:flags=bicubic,format=yuva420p[wm];" +
                 $"[{cur}][wm]overlay=x=W-w-{margin}:y=H-h-{margin},format={last}[v]");
        return g.ToString();
    }

    private static string CpuFilter(EncoderOptions o, bool fromGpuFrames)
    {
        var f = new List<string>();
        if (fromGpuFrames) { f.Add("hwdownload"); f.Add("format=bgra"); }
        // lanczos keeps text sharp when the picture is made smaller; bt709 gives correct HD colours
        f.Add(NeedsScale(o)
            ? $"scale={o.OutputWidth}:{o.OutputHeight}:flags=lanczos:out_color_matrix=bt709"
            : "scale=trunc(iw/2)*2:trunc(ih/2)*2:out_color_matrix=bt709");
        f.Add(IsHardware(o.EncoderId) ? "format=nv12" : "format=yuv420p");
        return string.Join(",", f);
    }

    private static void AppendEncoder(StringBuilder sb, EncoderOptions o)
    {
        int k = o.BitrateKbps, max = k * 3 / 2, buf = k * 2;
        var id = o.EncoderId;
        bool best = !o.BasicEncoderArgs;   // "best" adds optional quality switches; the app falls back to basic if a GPU rejects them
        // Constant quality is used only together with the "best" switches: if a GPU rejects it, the app retries in
        // BasicEncoderArgs mode, which uses the plain bitrate settings below.
        int cq = best ? o.Cq : 0;
        if (cq > 0) max = k * 2;           // CQ lets peaks go higher than the average bitrate would
        if (IsNvenc(id))
        {
            if (cq > 0) sb.Append($"-c:v {id} -preset p5 -rc vbr -cq {cq} -b:v 0 -maxrate {max}k -bufsize {buf}k ");
            else sb.Append($"-c:v {id} -preset p5 -rc vbr -b:v {k}k -maxrate {max}k -bufsize {buf}k ");
            if (best) sb.Append("-tune hq -spatial-aq 1 -rc-lookahead 16 ");
        }
        else if (IsQsv(id))
        {
            if (cq > 0) sb.Append($"-c:v {id} -preset slow -global_quality {cq} -maxrate {max}k -bufsize {buf}k ");
            else sb.Append($"-c:v {id} -preset {(best ? "slow" : "medium")} -b:v {k}k -maxrate {max}k -bufsize {buf}k ");
        }
        else if (IsAmf(id))
        {
            if (cq > 0) sb.Append($"-c:v {id} -quality quality -rc cqp -qp_i {cq} -qp_p {cq + 2} -qp_b {cq + 4} ");
            else sb.Append($"-c:v {id} -quality {(best ? "quality" : "balanced")} -rc vbr_peak -b:v {k}k -maxrate {max}k ");
        }
        else
        {
            var preset = string.IsNullOrWhiteSpace(o.X264Preset) ? "veryfast" : o.X264Preset;
            if (cq > 0) sb.Append($"-c:v libx264 -preset {preset} -profile:v high -crf {cq} -maxrate {max}k -bufsize {buf}k ");
            else sb.Append($"-c:v libx264 -preset {preset} -profile:v high -b:v {k}k -maxrate {max}k -bufsize {buf}k ");
        }
    }
}
