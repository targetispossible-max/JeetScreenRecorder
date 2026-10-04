using System.Globalization;
using System.Text.RegularExpressions;
using JeetScreenRecorder.Utils;
using JeetScreenRecorder.VideoEncoding;

namespace JeetScreenRecorder.Recording;

/// <summary>
/// File layout inside the hidden "_parts" folder, per segment N:
///   partNNN.mkv  video only (written by ffmpeg)
///   partNNN.pcm  raw audio (written by the app)
///   partNNN.off  "audioDelaySeconds|sampleRate" (so a crashed recording can still be rebuilt)
///   avNNN.mkv    video + audio joined (result of <see cref="MuxAsync"/>)
/// </summary>
public static class SegmentMuxer
{
    private static readonly Regex SegmentName = new(@"^(?<kind>av|part)(?<n>\d+)\.mkv$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string VideoPath(string dir, int n) => Path.Combine(dir, $"part{n:000}.mkv");
    public static string PcmPath(string dir, int n) => Path.Combine(dir, $"part{n:000}.pcm");
    public static string InfoPath(string dir, int n) => Path.Combine(dir, $"part{n:000}.off");
    public static string AvPath(string dir, int n) => Path.Combine(dir, $"av{n:000}.mkv");

    public static void WriteInfo(string dir, int n, double delaySeconds, int sampleRate)
    {
        try
        {
            File.WriteAllText(InfoPath(dir, n),
                delaySeconds.ToString("0.###", CultureInfo.InvariantCulture) + "|" + sampleRate.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception ex) { AppLogger.Warn($"Could not write audio offset file: {ex.Message}"); }
    }

    public static (double Delay, int Rate) ReadInfo(string dir, int n, int defaultRate = 48000)
    {
        try
        {
            var p = InfoPath(dir, n);
            if (File.Exists(p))
            {
                var parts = File.ReadAllText(p).Trim().Split('|');
                double d = double.Parse(parts[0], CultureInfo.InvariantCulture);
                int r = parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : defaultRate;
                return (Math.Max(0, d), r > 0 ? r : defaultRate);
            }
        }
        catch (Exception ex) { AppLogger.Warn($"Could not read audio offset file: {ex.Message}"); }
        return (0, defaultRate);
    }

    /// <summary>Joins video + audio of segment N. Returns the file to use for this segment (the joined one, or the plain video if joining failed).</summary>
    /// <param name="addSilenceIfNoAudio">true when the recording has audio but this segment captured none (keeps all segments identical).</param>
    public static async Task<string> MuxAsync(string dir, int n, double delaySeconds, int sampleRate, int audioKbps, bool addSilenceIfNoAudio)
    {
        var video = VideoPath(dir, n);
        var pcm = PcmPath(dir, n);
        var av = AvPath(dir, n);
        if (!File.Exists(video) || new FileInfo(video).Length == 0) return video;

        bool havePcm = File.Exists(pcm) && new FileInfo(pcm).Length > 4096;
        if (!havePcm && !addSilenceIfNoAudio)
        {
            TryDelete(pcm); TryDelete(InfoPath(dir, n));
            return video;
        }

        var args = FfmpegArgsBuilder.BuildMux(video, havePcm ? pcm : null, delaySeconds, sampleRate, audioKbps, av);
        AppLogger.Info($"Joining audio and video of segment {n}: {args}");
        var (code, err) = await FfmpegRunner.RunAsync(args, 600000);
        if (code == 0 && File.Exists(av) && new FileInfo(av).Length > 0)
        {
            TryDelete(video); TryDelete(pcm); TryDelete(InfoPath(dir, n));
            AppLogger.Info($"Segment {n} joined: {new FileInfo(av).Length} bytes (audio={(havePcm ? "recorded" : "silent")})");
            return av;
        }

        AppLogger.Error($"Joining audio and video failed for segment {n} (exit {code}): {err.Trim()}");
        TryDelete(av);
        return video;   // keep the raw files; the video is still usable
    }

    /// <summary>All segment files of a parts folder in recording order. If both av### and part### exist, the joined one wins.</summary>
    public static List<(int N, string Path, bool Joined)> ListSegments(string dir)
    {
        var best = new Dictionary<int, (string Path, bool Joined)>();
        foreach (var f in Directory.GetFiles(dir, "*.mkv"))
        {
            var m = SegmentName.Match(System.IO.Path.GetFileName(f));
            if (!m.Success) continue;
            int n = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
            bool joined = m.Groups["kind"].Value.Equals("av", StringComparison.OrdinalIgnoreCase);
            if (!best.TryGetValue(n, out var cur) || (joined && !cur.Joined)) best[n] = (f, joined);
        }
        return best.OrderBy(kv => kv.Key).Select(kv => (kv.Key, kv.Value.Path, kv.Value.Joined)).ToList();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
