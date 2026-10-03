using JeetScreenRecorder.Models;

namespace JeetScreenRecorder.Settings;

public interface ISettingsService
{
    RecordingSettings Current { get; }
    void Load();
    void Save();
}
