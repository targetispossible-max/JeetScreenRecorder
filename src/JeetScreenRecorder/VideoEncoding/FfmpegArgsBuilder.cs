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

    /// <summary>Scale only when the target is smaller than the source (never upscale).</summary>
    public static bool NeedsScale(EncoderOptions o) =>
        o.OutputWidth > 0 && o.OutputHeight > 0 &&
        o.OutputWidth <= o.SourceWidth && o.OutputHeight <= o.SourceHeight &&
        (o.OutputWidth < o.SourceWidth || o.OutputHeight < o.SourceHeight);

    public static (int Width, int Height) OutputSize(EncoderOptions o) =>
        NeedsScale(o) ? (o.OutputWidth, o.OutputHeight) : (o.SourceWidth & ~1, o.SourceHeight & ~1);

    public static bool HasWebcam(EncoderOptions o) => !string.IsNullOrWhiteSpace(o.WebcamName);

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

        // ---- video filters ----
        if (cam)
        {
            sb.Append($"-filter_complex \"{WebcamGraph(o, gpuFrames)}\" -map \"[v]\" ");
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
    /// make the audio exactly as long as the video. Pass pcmPath = null to add a silent track instead
    /// (so every segment has the same streams and can be joined later).
    /// </summary>
    public static string BuildMux(string videoPath, string? pcmPath, double audioDelaySeconds,
        int sampleRate, int audioKbps, string outputPath)
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
        sb.Append($"-map 0:v:0 -map 1:a:0 -c:v copy -c:a aac -b:a {audioKbps}k -shortest \"{outputPath}\"");
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

    private static string WebcamGraph(EncoderOptions o, bool fromGpuFrames)
    {
        var (w, _) = OutputSize(o);
        int camW = WebcamWidthFor(w, o.WebcamPercent);
        int margin = Math.Max(12, w / 80);

        var bg = new List<string>();
        if (fromGpuFrames) { bg.Add("hwdownload"); bg.Add("format=bgra"); }
        bg.Add(NeedsScale(o)
            ? $"scale={o.OutputWidth}:{o.OutputHeight}:flags=lanczos:out_color_matrix=bt709"
            : "scale=trunc(iw/2)*2:trunc(ih/2)*2:out_color_matrix=bt709");
        bg.Add("format=yuv420p");

        var cam = new List<string>();
        if (o.WebcamMirror) cam.Add("hflip");
        cam.Add($"scale={camW}:-2:flags=bicubic");
        cam.Add("format=yuv420p");

        string pos = o.WebcamCorner switch
        {
            WebcamCorner.BottomLeft => $"x={margin}:y=H-h-{margin}",
            WebcamCorner.TopRight => $"x=W-w-{margin}:y={margin}",
            WebcamCorner.TopLeft => $"x={margin}:y={margin}",
            _ => $"x=W-w-{margin}:y=H-h-{margin}"
        };
        string last = IsHardware(o.EncoderId) ? "nv12" : "yuv420p";
        return $"[0:v]{string.Join(",", bg)}[bg];[1:v]{string.Join(",", cam)}[cam];[bg][cam]overlay={pos},format={last}[v]";
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
        if (IsNvenc(id))
        {
            sb.Append($"-c:v {id} -preset p5 -rc vbr -b:v {k}k -maxrate {max}k -bufsize {buf}k ");
            if (best) sb.Append("-tune hq -spatial-aq 1 -rc-lookahead 16 ");
        }
        else if (IsQsv(id))
        {
            sb.Append($"-c:v {id} -preset {(best ? "slow" : "medium")} -b:v {k}k -maxrate {max}k -bufsize {buf}k ");
        }
        else if (IsAmf(id))
        {
            sb.Append($"-c:v {id} -quality {(best ? "quality" : "balanced")} -rc vbr_peak -b:v {k}k -maxrate {max}k ");
        }
        else
        {
            var preset = string.IsNullOrWhiteSpace(o.X264Preset) ? "veryfast" : o.X264Preset;
            sb.Append($"-c:v libx264 -preset {preset} -profile:v high -b:v {k}k -maxrate {max}k -bufsize {buf}k ");
        }
    }
}
