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
                if (Directory.GetFiles(dir, "part*.mkv").Any(f => new FileInfo(f).Length > 0))
                    result.Add(dir);
        }
        catch (Exception ex) { AppLogger.Warn($"Recovery scan failed: {ex.Message}"); }
        return result;
    }

    public static async Task<string> RecoverAsync(string partsDir, string outputFolder)
    {
        var parts = Directory.GetFiles(partsDir, "part*.mkv")
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        var name = Path.GetFileName(partsDir).TrimStart('.');
        if (name.EndsWith("_parts", StringComparison.Ordinal)) name = name[..^6];
        var final = Path.Combine(outputFolder, "Recovered_" + name + ".mp4");
        return await RecordingFinalizer.FinalizeAsync(parts, final, partsDir);
    }
}
