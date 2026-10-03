using JeetScreenRecorder.Models;
using JeetScreenRecorder.Settings;
using JeetScreenRecorder.Utils;
using JeetScreenRecorder.VideoEncoding;

namespace JeetScreenRecorder.Capture;

public interface IScreenshotService
{
    Task<string> CaptureAsync();
}

public sealed class ScreenshotService(ISettingsService settings, IMonitorService monitors) : IScreenshotService
{
    public async Task<string> CaptureAsync()
    {
        var s = settings.Current;
        if (!FfmpegLocator.Exists)
            throw new InvalidOperationException("FFmpeg was not found next to the application. Please reinstall Jeet Screen Recorder.");

        var dir = Path.Combine(s.OutputFolder, "Screenshots");
        Directory.CreateDirectory(dir);
        var fmt = s.ScreenshotFormat.ToLowerInvariant();
        var ext = fmt is "jpg" or "webp" ? fmt : "png";
        var path = Path.Combine(dir, OutputNaming.Generate("Screenshot_", ext, DateTime.Now));

        var o = CaptureOptionsFactory.Create(s, monitors.Get(s.MonitorIndex)) with { Fps = 1 };
        var (code, err) = await FfmpegRunner.RunAsync(FfmpegArgsBuilder.BuildScreenshot(o, path), 20000);
        if ((code != 0 || !File.Exists(path)) && o.Backend == CaptureBackend.DesktopDuplication && o.WindowHandle == 0)
        {
            AppLogger.Warn($"GPU screenshot failed, retrying with compatible capture: {err.Trim()}");
            o = o with { Backend = CaptureBackend.Gdigrab };
            (code, err) = await FfmpegRunner.RunAsync(FfmpegArgsBuilder.BuildScreenshot(o, path), 20000);
        }
        if (code != 0 || !File.Exists(path))
            throw new InvalidOperationException($"Screenshot failed. {err.Trim()}");

        AppLogger.Info($"Screenshot saved: {path}");
        return path;
    }
}
