using System.Text;
using JeetScreenRecorder.Models;

namespace JeetScreenRecorder.VideoEncoding;

public static class FfmpegArgsBuilder
{
    public static bool IsNvenc(string id) => id.EndsWith("_nvenc", StringComparison.Ordinal);
    private static bool IsQsv(string id) => id.EndsWith("_qsv", StringComparison.Ordinal);
    private static bool IsAmf(string id) => id.EndsWith("_amf", StringComparison.Ordinal);
    private static bool IsHardware(string id) => IsNvenc(id) || IsQsv(id) || IsAmf(id);

    public static double QualityMultiplier(QualityPreset q) => q switch
    {
        QualityPreset.Low => 0.4,
        QualityPreset.Medium => 0.7,
        QualityPreset.High => 1.0,
        QualityPreset.VeryHigh => 1.6,
        QualityPreset.Lossless => 3.0, // near-lossless (very high bitrate)
        _ => 1.0
    };

    /// <summary>Scale only when the target is smaller than the source (never upscale).</summary>
    public static bool NeedsScale(EncoderOptions o) =>
        o.OutputWidth > 0 && o.OutputHeight > 0 &&
        o.OutputWidth <= o.SourceWidth && o.OutputHeight <= o.SourceHeight &&
        (o.OutputWidth < o.SourceWidth || o.OutputHeight < o.SourceHeight);

    public static (int Width, int Height) OutputSize(EncoderOptions o) =>
        NeedsScale(o) ? (o.OutputWidth, o.OutputHeight) : (o.SourceWidth, o.SourceHeight);

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

    public static string Build(EncoderOptions o, string? outputPath, bool test = false)
    {
        var sb = new StringBuilder("-hide_banner -y -loglevel error ");
        if (!test) sb.Append("-progress pipe:1 -nostats -stats_period 0.1 ");
        bool gpuFrames = o.Backend == CaptureBackend.DesktopDuplication && o.WindowHandle == 0;

        // ---- inputs (all inputs must come before any output option) ----
        AppendVideoInput(sb, o, o.Fps, o.DrawMouse);

        // ---- video filters ----
        // NVENC can take GPU frames directly (zero-copy) when no scaling is needed.
        bool cpuPath = !gpuFrames || !IsNvenc(o.EncoderId) || NeedsScale(o);
        if (cpuPath) sb.Append($"-vf \"{CpuFilter(o, gpuFrames)}\" ");

        AppendEncoder(sb, o);
        sb.Append($"-g {o.Fps * 2} ");

        if (test) sb.Append("-frames:v 5 -f null -");
        else sb.Append($"\"{outputPath}\"");
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

    private static string CpuFilter(EncoderOptions o, bool fromGpuFrames)
    {
        var f = new List<string>();
        if (fromGpuFrames) { f.Add("hwdownload"); f.Add("format=bgra"); }
        f.Add(NeedsScale(o)
            ? $"scale={o.OutputWidth}:{o.OutputHeight}:flags=bicubic"
            : "scale=trunc(iw/2)*2:trunc(ih/2)*2");
        f.Add(IsHardware(o.EncoderId) ? "format=nv12" : "format=yuv420p");
        return string.Join(",", f);
    }

    private static void AppendEncoder(StringBuilder sb, EncoderOptions o)
    {
        int k = o.BitrateKbps, max = k * 3 / 2, buf = k * 2;
        var id = o.EncoderId;
        if (IsNvenc(id))
            sb.Append($"-c:v {id} -preset p4 -rc vbr -b:v {k}k -maxrate {max}k -bufsize {buf}k ");
        else if (IsQsv(id))
            sb.Append($"-c:v {id} -preset medium -b:v {k}k -maxrate {max}k -bufsize {buf}k ");
        else if (IsAmf(id))
            sb.Append($"-c:v {id} -quality balanced -rc vbr_peak -b:v {k}k -maxrate {max}k ");
        else
            sb.Append($"-c:v libx264 -preset veryfast -b:v {k}k -maxrate {max}k -bufsize {buf}k ");
    }
}
