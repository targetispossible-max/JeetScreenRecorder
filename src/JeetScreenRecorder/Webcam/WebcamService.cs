using System.Text.RegularExpressions;
using JeetScreenRecorder.Utils;
using JeetScreenRecorder.VideoEncoding;

namespace JeetScreenRecorder.Webcam;

/// <summary>Finds cameras and tests them with the bundled ffmpeg (DirectShow), so no extra software is needed.</summary>
public sealed class WebcamService : IWebcamService
{
    private static readonly Regex VideoDevice = new("\"(?<name>[^\"]+)\"\\s+\\(video\\)", RegexOptions.Compiled);

    /// <summary>Reads camera names out of the text printed by <c>ffmpeg -list_devices true -f dshow -i dummy</c>.</summary>
    public static IReadOnlyList<CameraInfo> ParseDevices(string ffmpegOutput)
    {
        var list = new List<CameraInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in VideoDevice.Matches(ffmpegOutput ?? ""))
        {
            var name = m.Groups["name"].Value.Trim();
            if (name.Length == 0 || name.StartsWith("@device", StringComparison.OrdinalIgnoreCase)) continue;
            if (seen.Add(name)) list.Add(new CameraInfo(name, name));
        }
        return list;
    }

    public async Task<IReadOnlyList<CameraInfo>> DetectAsync()
    {
        if (!FfmpegLocator.Exists) return Array.Empty<CameraInfo>();
        try
        {
            var (_, text) = await FfmpegRunner.RunAsync("-hide_banner -list_devices true -f dshow -i dummy", 15000);
            var cams = ParseDevices(text);
            AppLogger.Info("Cameras: " + (cams.Count == 0 ? "none found" : string.Join(" | ", cams.Select(c => c.Name))));
            return cams;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Camera detection failed", ex);
            return Array.Empty<CameraInfo>();
        }
    }

    public async Task<(string? Path, string Error)> TakeSnapshotAsync(string cameraName)
    {
        if (!FfmpegLocator.Exists) return (null, "FFmpeg was not found next to the application.");
        var path = Path.Combine(Path.GetTempPath(), $"jeet_cam_{Guid.NewGuid():N}.jpg");
        try
        {
            var (code, err) = await FfmpegRunner.RunAsync(FfmpegArgsBuilder.BuildCameraSnapshot(cameraName, path, autoFormat: false), 15000);
            if (code != 0 || !File.Exists(path))
            {
                // Some cameras do not offer 1280x720 @ 30 - let the camera pick its own format.
                (code, err) = await FfmpegRunner.RunAsync(FfmpegArgsBuilder.BuildCameraSnapshot(cameraName, path, autoFormat: true), 15000);
            }
            if (code == 0 && File.Exists(path)) return (path, "");
            var last = (err ?? "").Trim().Split('\n').LastOrDefault()?.Trim() ?? "";
            AppLogger.Warn($"Camera test failed: {err?.Trim()}");
            return (null, "The camera could not be opened. Close other apps that use the camera (Zoom, Teams, Camera app) " +
                          "and check Windows Settings > Privacy > Camera. " + last);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Camera test failed", ex);
            return (null, ex.Message);
        }
    }
}
