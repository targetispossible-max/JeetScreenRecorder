using System.Diagnostics;
using System.Text;
using JeetScreenRecorder.Utils;
using JeetScreenRecorder.VideoEncoding;

namespace JeetScreenRecorder.Webcam;

/// <summary>
/// Opens the camera with the bundled ffmpeg (DirectShow) and delivers live pictures (960x540, BGRA).
/// The floating webcam window shows them; the screen recording then records that window like any other window.
/// </summary>
public sealed class WebcamFeed : IDisposable
{
    public const int FrameWidth = 960;
    public const int FrameHeight = 540;
    public const int Stride = FrameWidth * 4;
    private const int FrameBytes = Stride * FrameHeight;

    private Process? _proc;
    private volatile bool _stop;
    private byte[] _front = new byte[FrameBytes];
    private byte[] _back = new byte[FrameBytes];

    /// <summary>Lock this while reading <see cref="Front"/>.</summary>
    public object Sync { get; } = new();
    /// <summary>The newest complete picture.</summary>
    public byte[] Front => _front;

    /// <summary>A new picture is ready (raised on a background thread).</summary>
    public event Action? FrameReady;
    /// <summary>The camera stopped after it had started (raised on a background thread).</summary>
    public event Action<string>? Failed;

    /// <summary>Starts the camera. Returns null when it works, otherwise the reason it failed.</summary>
    public async Task<string?> StartAsync(string cameraName, bool mirror)
    {
        Stop();
        if (!FfmpegLocator.Exists) return "FFmpeg was not found next to the application.";
        _stop = false;

        string last = "";
        foreach (bool auto in new[] { false, true })   // second try: let the camera choose its own format
        {
            var (p, err) = await TryLaunchAsync(cameraName, mirror, auto);
            if (p != null)
            {
                _proc = p;
                var t = new Thread(() => ReadLoop(p)) { IsBackground = true, Name = "Webcam reader" };
                t.Start();
                return null;
            }
            last = err;
        }
        AppLogger.Warn("Webcam window: camera could not be opened: " + last);
        var line = last.Trim().Split('\n').LastOrDefault()?.Trim() ?? "";
        return "The camera could not be opened. Close other apps that use it (Zoom, Teams, Camera app) and check " +
               "Windows Settings > Privacy > Camera. " + line;
    }

    private static string BuildArgs(string cameraName, bool mirror, bool auto)
    {
        var name = cameraName.Replace("\"", "");
        var sb = new StringBuilder("-hide_banner -loglevel error -f dshow -rtbufsize 64M ");
        if (!auto) sb.Append("-video_size 1280x720 -framerate 30 ");
        sb.Append($"-i \"video={name}\" ");
        var vf = new List<string>();
        if (mirror) vf.Add("hflip");
        // fill the 16:9 frame (crop the sides of a 4:3 camera instead of squeezing it)
        vf.Add($"scale={FrameWidth}:{FrameHeight}:force_original_aspect_ratio=increase");
        vf.Add($"crop={FrameWidth}:{FrameHeight}");
        vf.Add("format=bgra");
        sb.Append($"-vf \"{string.Join(",", vf)}\" -f rawvideo -pix_fmt bgra pipe:1");
        return sb.ToString();
    }

    private static async Task<(Process? P, string Err)> TryLaunchAsync(string cameraName, bool mirror, bool auto)
    {
        var psi = new ProcessStartInfo(FfmpegLocator.FfmpegPath, BuildArgs(cameraName, mirror, auto))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        var p = new Process { StartInfo = psi };
        var err = new StringBuilder();
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (err) err.AppendLine(e.Data); };
        p.Start();
        p.BeginErrorReadLine();

        // If ffmpeg is still running after 1.5 s the camera opened fine.
        bool died = await Task.Run(() => p.WaitForExit(1500));
        if (!died) return (p, "");
        p.WaitForExit();
        string text;
        lock (err) text = err.ToString();
        p.Dispose();
        return (null, text);
    }

    private void ReadLoop(Process p)
    {
        bool ended = false;
        try
        {
            var s = p.StandardOutput.BaseStream;
            while (!_stop && !ended)
            {
                int got = 0;
                while (got < FrameBytes)
                {
                    int n = s.Read(_back, got, FrameBytes - got);
                    if (n <= 0) { ended = true; break; }
                    got += n;
                }
                if (ended) break;
                lock (Sync) { (_front, _back) = (_back, _front); }
                FrameReady?.Invoke();
            }
        }
        catch { ended = true; }

        if (!_stop && ended) Failed?.Invoke("The camera stopped working (it may have been unplugged or taken by another app).");
    }

    public void Stop()
    {
        _stop = true;
        var p = _proc;
        _proc = null;
        if (p == null) return;
        try
        {
            if (!p.HasExited)
            {
                try { p.StandardInput.WriteLine("q"); p.StandardInput.Flush(); } catch { }
                if (!p.WaitForExit(1500)) p.Kill(true);
            }
        }
        catch { }
        try { p.Dispose(); } catch { }
    }

    public void Dispose() => Stop();
}
