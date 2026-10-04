using JeetScreenRecorder.Utils;

namespace JeetScreenRecorder.Recording;

/// <summary>Finds unfinished recordings (left behind by a crash) and joins their parts into one file.</summary>
public static class RecordingRecovery
{
    public static IReadOnlyList<string> FindIncomplete(string outputFolder)
    {
        var result = new List<string>();
        try
        {
            if (!Directory.Exists(outputFolder)) return result;
            foreach (var dir in Directory.GetDirectories(outputFolder, ".*_parts"))
                if (SegmentMuxer.ListSegments(dir).Any(s => new FileInfo(s.Path).Length > 0))
                    result.Add(dir);
        }
        catch (Exception ex) { AppLogger.Warn($"Recovery scan failed: {ex.Message}"); }
        return result;
    }

    public static async Task<string> RecoverAsync(string partsDir, string outputFolder)
    {
        var segments = SegmentMuxer.ListSegments(partsDir);
        // Did this recording have audio at all? (a joined segment, or at least one raw audio file)
        bool hasAudio = segments.Any(s => s.Joined) ||
                        Directory.GetFiles(partsDir, "part*.pcm").Any(f => new FileInfo(f).Length > 4096);

        var files = new List<string>();
        foreach (var (n, path, joined) in segments)
        {
            if (joined) { files.Add(path); continue; }
            var (delay, rate) = SegmentMuxer.ReadInfo(partsDir, n);
            files.Add(await SegmentMuxer.MuxAsync(partsDir, n, delay, rate, 192, hasAudio));
        }

        var name = Path.GetFileName(partsDir).TrimStart('.');
        if (name.EndsWith("_parts", StringComparison.Ordinal)) name = name[..^6];
        var final = Path.Combine(outputFolder, "Recovered_" + name + ".mp4");
        return await RecordingFinalizer.FinalizeAsync(files, final, partsDir);
    }
}
