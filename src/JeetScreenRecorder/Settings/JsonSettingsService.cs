using System.Text.Json;
using System.Text.Json.Serialization;
using JeetScreenRecorder.Models;
using JeetScreenRecorder.Utils;

namespace JeetScreenRecorder.Settings;

public sealed class JsonSettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;
    public RecordingSettings Current { get; private set; } = new();

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "JeetScreenRecorder", "settings.json");

    public JsonSettingsService() : this(DefaultPath) { }

    public JsonSettingsService(string path)
    {
        _path = path;
        Load();
    }

    public void Load()
    {
        try
        {
            if (File.Exists(_path))
                Current = JsonSerializer.Deserialize<RecordingSettings>(File.ReadAllText(_path), Options) ?? new();
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed to load settings, using defaults", ex);
            Current = new();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Options));
        File.Move(tmp, _path, overwrite: true);
    }
}
