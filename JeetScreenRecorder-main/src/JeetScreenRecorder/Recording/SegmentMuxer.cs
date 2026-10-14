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

    public static string WallPath(string dir, int n) => Path.Combine(dir, $"part{n:000}.wall");

    public static void WriteWall(string dir, int n, double wallSeconds)
    {
        try { File.WriteAllText(WallPath(dir, n), wallSeconds.ToString("0.###", CultureInfo.InvariantCulture)); }
        catch (Exception ex) { AppLogger.Warn($"Could not write real-time file: {ex.Message}"); }
    }

    public static double ReadWall(string dir, int n)
    {
        try
        {
            var p = WallPath(dir, n);
            if (File.Exists(p)) return double.Parse(File.ReadAllText(p).Trim(), CultureInfo.InvariantCulture);
        }
        catch { }
        return 0;
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
    /// <param name="wallSeconds">Real (wall-clock) time the segment was recorded; 0 = unknown. Used to repair videos that came out shorter than the real recording time.</param>
    public static async Task<string> MuxAsync(string dir, int n, double delaySeconds, int sampleRate, int audioKbps, bool addSilenceIfNoAudio, double wallSeconds = 0)
    {
        var video = VideoPath(dir, n);
        var pcm = PcmPath(dir, n);
        var av = AvPath(dir, n);
        if (!File.Exists(video) || new FileInfo(video).Length == 0) return video;

        bool havePcm = File.Exists(pcm) && new FileInfo(pcm).Length > 4096;
        double stretch = await ComputeStretchAsync(video, n, wallSeconds);
        if (!havePcm && !addSilenceIfNoAudio && stretch <= 1.0)
        {
            TryDelete(pcm); TryDelete(InfoPath(dir, n));
            return video;
        }

        var args = FfmpegArgsBuilder.BuildMux(video, havePcm ? pcm : null, delaySeconds, sampleRate, audioKbps, av, stretch);
        AppLogger.Info($"Joining audio and video of segment {n}: {args}");
        var (code, err) = await FfmpegRunner.RunAsync(args, 600000);
        if (code == 0 && File.Exists(av) && new FileInfo(av).Length > 0)
        {
            TryDelete(video); TryDelete(pcm); TryDelete(InfoPath(dir, n)); TryDelete(WallPath(dir, n));
            AppLogger.Info($"Segment {n} joined: {new FileInfo(av).Length} bytes (audio={(havePcm ? "recorded" : "silent")})");
            return av;
        }

        AppLogger.Error($"Joining audio and video failed for segment {n} (exit {code}): {err.Trim()}");
        TryDelete(av);
        return video;   // keep the raw files; the video is still usable
    }

    /// <summary>
    /// Compares the real recording time of a segment with the length of its video file. When the video is clearly shorter
    /// (capture/encoder could not keep real-time speed) the factor to stretch it back is returned, otherwise 1.
    /// </summary>
    public static async Task<double> ComputeStretchAsync(string video, int n, double wallSeconds)
    {
        if (wallSeconds < MinWallForStretch) return 1.0;
        double len = await ProbeDurationAsync(video);
        if (len <= 0) return 1.0;
        double ratio = wallSeconds / len;
        AppLogger.Info($"Segment {n}: real time {wallSeconds:0.0}s, video length {len:0.0}s, ratio {ratio:0.000}");
        return ratio is >= StretchThreshold and <= 20.0 ? ratio : 1.0;
    }

    public const double MinWallForStretch = 20.0;
    public const double StretchThreshold = 1.03;

    private static readonly Regex DurationLine = new(@"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)", RegexOptions.Compiled);

    /// <summary>Video length in seconds (read from the ffmpeg banner); 0 when unknown.</summary>
    public static async Task<double> ProbeDurationAsync(string file)
    {
        try
        {
            var (_, err) = await FfmpegRunner.RunAsync($"-hide_banner -i \"{file}\"", 30000);
            var m = DurationLine.Match(err);
            if (!m.Success) return 0;
            return int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 3600
                 + int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * 60
                 + double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) { AppLogger.Warn($"Could not read video length: {ex.Message}"); return 0; }
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
