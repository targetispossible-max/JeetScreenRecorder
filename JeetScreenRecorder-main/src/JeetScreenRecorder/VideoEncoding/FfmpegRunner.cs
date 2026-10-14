using System.Diagnostics;
using System.Text;

namespace JeetScreenRecorder.VideoEncoding;

public static class FfmpegRunner
{
    public static async Task<(int ExitCode, string StdErr)> RunAsync(string args, int timeoutMs = 60000)
    {
        var psi = new ProcessStartInfo(FfmpegLocator.FfmpegPath, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        using var p = new Process { StartInfo = psi };
        var err = new StringBuilder();
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (err) err.AppendLine(e.Data); };
        p.OutputDataReceived += (_, _) => { };
        p.Start();
        p.BeginErrorReadLine();
        p.BeginOutputReadLine();
        var exited = await Task.Run(() => p.WaitForExit(timeoutMs));
        if (!exited)
        {
            try { p.Kill(true); } catch { }
            return (-1, "ffmpeg timed out");
        }
        p.WaitForExit();
        return (p.ExitCode, err.ToString());
    }
}
