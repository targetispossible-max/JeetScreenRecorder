using Microsoft.Win32;

namespace JeetScreenRecorder.Utils;

/// <summary>
/// Remembers the day the app was first started on this PC. From day 8 on, every recording gets the Jeet watermark.
/// The date is kept in two places (registry + AppData file), the earlier date wins, and a PC clock that is moved
/// back is ignored. Nothing is shown in the user interface.
/// </summary>
public static class TrialPolicy
{
    public const int FreeDays = 7;
    private const string RegKey = @"Software\JeetScreenRecorder";
    private const string RegValue = "InstallStamp";

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JeetScreenRecorder", "app.dat");

    /// <summary>Call once at startup; stores the first-run date if this is the first start.</summary>
    public static void EnsureStarted() => _ = FirstRunUtc();

    public static bool WatermarkRequired => WatermarkRequiredAt(DateTime.UtcNow);

    public static bool WatermarkRequiredAt(DateTime nowUtc)
    {
        try
        {
            var first = FirstRunUtc();
            var last = LastSeenUtc();
            var now = nowUtc < last ? last : nowUtc;       // clock moved back -> use the latest date ever seen
            TouchLastSeen(now);
            return (now - first).TotalDays >= FreeDays;
        }
        catch (Exception ex) { AppLogger.Warn($"Date check failed: {ex.Message}"); return false; }
    }

    private static DateTime FirstRunUtc()
    {
        DateTime? reg = ReadReg(RegValue), file = ReadFile(0);
        DateTime first;
        if (reg != null && file != null) first = reg < file ? reg.Value : file.Value;
        else first = reg ?? file ?? DateTime.UtcNow;
        if (reg == null || reg != first) WriteReg(RegValue, first);
        if (file == null || file != first) WriteFile(0, first);
        return first;
    }

    private static DateTime LastSeenUtc()
    {
        DateTime? reg = ReadReg("LastStamp"), file = ReadFile(1);
        if (reg != null && file != null) return reg > file ? reg.Value : file.Value;
        return reg ?? file ?? DateTime.MinValue;
    }

    private static void TouchLastSeen(DateTime now)
    {
        WriteReg("LastStamp", now);
        WriteFile(1, now);
    }

    // ---- storage (ticks as text; deliberately not named like a trial) ----
    private static DateTime? ReadReg(string name)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RegKey);
            return k?.GetValue(name) is string s && long.TryParse(s, out var t) ? new DateTime(t, DateTimeKind.Utc) : null;
        }
        catch { return null; }
    }

    private static void WriteReg(string name, DateTime v)
    {
        try { using var k = Registry.CurrentUser.CreateSubKey(RegKey); k.SetValue(name, v.Ticks.ToString()); } catch { }
    }

    private static DateTime? ReadFile(int line)
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var lines = File.ReadAllLines(FilePath);
            return lines.Length > line && long.TryParse(lines[line], out var t) ? new DateTime(t, DateTimeKind.Utc) : null;
        }
        catch { return null; }
    }

    private static void WriteFile(int line, DateTime v)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var lines = File.Exists(FilePath) ? File.ReadAllLines(FilePath).ToList() : new List<string>();
            while (lines.Count <= line) lines.Add("");
            lines[line] = v.Ticks.ToString();
            File.WriteAllLines(FilePath, lines);
        }
        catch { }
    }
}
