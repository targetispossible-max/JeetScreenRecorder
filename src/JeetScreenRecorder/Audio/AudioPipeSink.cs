using System.IO.Pipes;

namespace JeetScreenRecorder.Audio;

/// <summary>A named pipe that ffmpeg reads raw PCM audio from.</summary>
public sealed class AudioPipeSink : IDisposable
{
    private readonly NamedPipeServerStream _server;
    public string PipeName { get; } = "jeet_audio_" + Guid.NewGuid().ToString("N");
    public string FfmpegPath => @"\\.\pipe\" + PipeName;
    public Stream Stream => _server;

    public AudioPipeSink()
    {
        _server = new NamedPipeServerStream(PipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.None, 0, 262144);
    }

    public async Task ConnectAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        await _server.WaitForConnectionAsync(cts.Token);
    }

    public void Dispose()
    {
        try { _server.Dispose(); } catch { }
    }
}
