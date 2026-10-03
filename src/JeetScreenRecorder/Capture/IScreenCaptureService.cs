using JeetScreenRecorder.Models;

namespace JeetScreenRecorder.Capture;

public sealed record MonitorInfo(int Index, string Name, int Width, int Height, int RefreshRate, bool IsPrimary, int X, int Y, string DeviceName);
public sealed record CaptureRegion(int X, int Y, int Width, int Height);
public sealed record CaptureTarget(CaptureSource Source, int MonitorIndex, nint WindowHandle, CaptureRegion? Region);

public interface IScreenCaptureService : IAsyncDisposable
{
    IReadOnlyList<MonitorInfo> GetMonitors();
    Task StartAsync(CaptureTarget target, int fps, CancellationToken ct);
    Task StopAsync();
    double ActualFps { get; }
    long DroppedFrames { get; }
}
