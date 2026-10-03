namespace JeetScreenRecorder.Audio;

public enum AudioDeviceKind { Microphone, SystemOutput }
public sealed record AudioDeviceInfo(string Id, string Name, AudioDeviceKind Kind);

public interface IAudioCaptureService : IDisposable
{
    IReadOnlyList<AudioDeviceInfo> GetMicrophones();
    bool IsRunning { get; }
    event EventHandler<string>? Warning;
    void Start(string? micDeviceId, bool mic, bool system, int sampleRate);
    void Stop();
    void SetGains(double micGain, double systemGain);
    void SetEnabled(bool mic, bool system);
    void SetMicDevice(string? micDeviceId);
    /// <summary>Receives mixed PCM (s16le, stereo). Pass null to stop writing.</summary>
    void SetSink(Stream? sink);
    /// <summary>Peak levels (0..1) since the last call.</summary>
    (double Mic, double System) ReadPeaks();
}
