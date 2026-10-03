using JeetScreenRecorder.Models;

namespace JeetScreenRecorder.VideoEncoding;

public sealed record EncoderInfo(string Id, string DisplayName, VideoCodec Codec, bool IsHardware);
public sealed record EncoderStats(double Fps, long SizeBytes, long DroppedFrames, long Frames);
public sealed class EncoderStartException(string message) : Exception(message);

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
