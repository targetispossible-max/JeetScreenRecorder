using System.Text;
using JeetScreenRecorder.Utils;
using JeetScreenRecorder.VideoEncoding;

namespace JeetScreenRecorder.Recording;

public static class ConcatList
{
    public static string Build(IEnumerable<string> paths) =>
        string.Join("\n", paths.Select(p => "file '" + p.Replace('\\', '/').Replace("'", "'\\''") + "'")) + "\n";
}

public static class RecordingFinalizer
{
    /// <summary>Joins all segments (pause/resume parts) into ONE final file without re-encoding.</summary>
    public static async Task<string> FinalizeAsync(IReadOnlyList<string> segments, string finalPath, string partsDir)
    {
        var valid = segments.Where(File.Exists).Where(p => new FileInfo(p).Length > 0).ToList();
        if (valid.Count == 0) throw new InvalidOperationException("No video data was recorded.");

        var ext = Path.GetExtension(finalPath).ToLowerInvariant();
        if (valid.Count == 1 && ext == ".mkv")
        {
            File.Move(valid[0], finalPath, true);
        }
        else
        {
            var list = Path.Combine(partsDir, "list.txt");
            File.WriteAllText(list, ConcatList.Build(valid), new UTF8Encoding(false));
            var faststart = ext is ".mp4" or ".mov" ? "-movflags +faststart " : "";
            var args = $"-hide_banner -y -loglevel error -f concat -safe 0 -i \"{list}\" -c copy {faststart}\"{finalPath}\"";
            var (code, err) = await FfmpegRunner.RunAsync(args, 600000);
            if (code != 0 || !File.Exists(finalPath))
                throw new InvalidOperationException($"Joining the video parts failed: {err.Trim()}");
        }

        try { Directory.Delete(partsDir, true); } catch (Exception ex) { AppLogger.Warn($"Could not delete temp parts: {ex.Message}"); }
        return finalPath;
    }
}
