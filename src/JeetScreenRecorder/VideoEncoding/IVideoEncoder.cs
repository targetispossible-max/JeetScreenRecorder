using JeetScreenRecorder.Models;

namespace JeetScreenRecorder.VideoEncoding;

public sealed record EncoderInfo(string Id, string DisplayName, VideoCodec Codec, bool IsHardware, bool BasicOnly = false);
public sealed record EncoderStats(double Fps, long SizeBytes, long DroppedFrames, long Frames, double OutTimeSeconds = 0);
public sealed class EncoderStartException(string message, string details = "") : Exception(message)
{
    /// <summary>The complete ffmpeg error text (used to find out which part - camera, screen, encoder - failed).</summary>
    public string Details { get; } = details;
}

public interface IVideoEncoder : IAsyncDisposable
{
    event EventHandler<EncoderStats>? StatsUpdated;
    Task StartAsync(EncoderOptions options, string outputPath);
    Task StopAsync();
}

public interface IEncoderDetector
{
    Task<IReadOnlyList<EncoderInfo>> DetectAsync();
}
