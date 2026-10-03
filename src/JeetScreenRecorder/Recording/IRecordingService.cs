using JeetScreenRecorder.Models;

namespace JeetScreenRecorder.Recording;

public sealed record RecordingStats(double Fps, long SizeBytes, long Dropped, string Resolution)
{
    public static RecordingStats Empty { get; } = new(0, 0, 0, "");
}

public interface IRecordingService
{
    RecordingState State { get; }
    TimeSpan Elapsed { get; }
    RecordingStats Stats { get; }
    string EncoderName { get; }
    string CaptureName { get; }
    string? LastOutputPath { get; }
    event EventHandler? StateChanged;
    event EventHandler<string>? Notice;
    Task InitializeAsync();
    Task StartAsync();
    Task PauseAsync();
    Task ResumeAsync();
    Task StopAsync();
}
