using System.Text.Json;

namespace JeetScreenRecorder.Licensing;

/// <summary>
/// A UUID that is generated once and stored in %APPDATA%\JeetScreenRecorder\installation_id.json.
/// It is NOT the device fingerprint (which is hardware-derived); this one identifies
/// a particular installation and survives hardware changes but not a full wipe of AppData.
/// The server uses both together to track trial usage.
/// </summary>
internal static class InstallationIdProvider
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "JeetScreenRecorder",
        "installation_id.json");

    private static string? _cached;

    public static string GetInstallationId()
    {
        if (_cached != null) return _cached;

        try
        {
            if (File.Exists(FilePath))
            {
                var text = File.ReadAllText(FilePath);
                var doc  = JsonSerializer.Deserialize<IdFile>(text);
                if (doc?.id is { Length: 36 } id)
                {
                    _cached = id;
                    return _cached;
                }
            }
        }
        catch { /* will regenerate */ }

        // Generate and persist a new UUID
        _cached = Guid.NewGuid().ToString("D");
        PersistId(_cached);
        return _cached;
    }

    private static void PersistId(string id)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var json = JsonSerializer.Serialize(new IdFile { id = id });
            File.WriteAllText(FilePath, json);
        }
        catch { /* non-fatal */ }
    }

    private sealed class IdFile
    {
        public string id { get; set; } = "";
    }
}
