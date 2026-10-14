using System.Diagnostics;
using System.Globalization;
using System.Text;
using JeetScreenRecorder.Models;
using JeetScreenRecorder.Utils;

namespace JeetScreenRecorder.VideoEncoding;

/// <summary>Runs one ffmpeg process that captures the screen and encodes into one segment file.</summary>
public sealed class FfmpegSegmentEncoder : IVideoEncoder
{
    private Process? _proc;
    private readonly StringBuilder _err = new();
    private double _fps;
    private long _size, _drop, _frames;
    private double _outTime;

    public event EventHandler<EncoderStats>? StatsUpdated;

    public async Task StartAsync(EncoderOptions options, string outputPath)
    {
        if (!FfmpegLocator.Exists)
            throw new EncoderStartException("FFmpeg was not found next to the application. Please reinstall Jeet Screen Recorder.");

        var args = FfmpegArgsBuilder.Build(options, outputPath);
        AppLogger.Info($"Starting ffmpeg: {args}");
        var psi = new ProcessStartInfo(FfmpegLocator.FfmpegPath, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var exited = new TaskCompletionSource<int>();
        p.OutputDataReceived += OnOutput;
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (_err) _err.AppendLine(e.Data); };
        p.Exited += (_, _) => exited.TrySetResult(p.ExitCode);
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        _proc = p;

        var first = await Task.WhenAny(exited.Task, Task.Delay(1500));
        if (first == exited.Task)
        {
            await Task.Run(() => p.WaitForExit()); // let all stderr lines arrive
            string text;
            lock (_err) text = _err.ToString().Trim();
            AppLogger.Info($"ffmpeg full output: {text}");
            var last = text.Split('\n').LastOrDefault()?.Trim() ?? "";
            _proc = null;
            p.Dispose();
            throw new EncoderStartException($"The video encoder could not start. {last}", text);
        }
    }

    private void OnOutput(object sender, DataReceivedEventArgs e)
    {
        if (e.Data is null) return;
        int i = e.Data.IndexOf('=');
        if (i < 0) return;
        var key = e.Data[..i];
        var val = e.Data[(i + 1)..].Trim();
        switch (key)
        {
            case "fps": double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out _fps); break;
            case "total_size": long.TryParse(val, out _size); break;
            case "drop_frames": long.TryParse(val, out _drop); break;
            case "frame": long.TryParse(val, out _frames); break;
            case "out_time_us": if (long.TryParse(val, out var us) && us > 0) _outTime = us / 1_000_000.0; break;
            case "progress": StatsUpdated?.Invoke(this, new EncoderStats(_fps, _size, _drop, _frames, _outTime)); break;
        }
    }

    public async Task StopAsync()
    {
        var p = _proc;
        if (p == null) return;
        _proc = null;
        try
        {
            if (!p.HasExited)
            {
                await p.StandardInput.WriteLineAsync("q");
                await p.StandardInput.FlushAsync();
            }
        }
        catch { /* process may already be gone */ }

        var done = await Task.Run(() => p.WaitForExit(15000));
        if (!done)
        {
            AppLogger.Warn("ffmpeg did not exit in time and was killed");
            try { p.Kill(true); } catch { }
        }
        p.Dispose();
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
