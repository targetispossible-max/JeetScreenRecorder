using System.Net;
using System.Net.Sockets;
using System.Text;

namespace JeetScreenRecorder.Audio;

/// <summary>
/// A local (loopback-only) TCP endpoint that ffmpeg connects to and reads audio from.
/// The audio is sent as a streaming WAV (header + raw 16-bit stereo PCM), so ffmpeg needs no extra audio flags.
/// (Replaces the former Windows named pipe, which ffmpeg could not open.)
/// </summary>
public sealed class AudioPipeSink : IDisposable
{
    private readonly TcpListener _listener;
    private readonly int _sampleRate;
    private TcpClient? _client;
    private NetworkStream? _stream;

    /// <summary>The input address handed to ffmpeg (-i).</summary>
    public string FfmpegPath { get; }

    /// <summary>Valid only after <see cref="ConnectAsync"/> has completed.</summary>
    public Stream Stream => _stream ?? throw new InvalidOperationException("Audio is not connected yet.");

    public AudioPipeSink(int sampleRate = 48000)
    {
        _sampleRate = sampleRate;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start(1);
        int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        FfmpegPath = $"tcp://127.0.0.1:{port}";
    }

    /// <summary>Waits for ffmpeg to connect, then sends the WAV header.</summary>
    public async Task ConnectAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        _client = await _listener.AcceptTcpClientAsync(cts.Token);
        _client.NoDelay = true;
        _stream = _client.GetStream();
        try { _listener.Stop(); } catch { }

        var header = BuildWavHeader(_sampleRate);
        await _stream.WriteAsync(header, cts.Token);
        await _stream.FlushAsync(cts.Token);
    }

    /// <summary>44-byte WAV header for 16-bit stereo PCM of unknown (streaming) length.</summary>
    private static byte[] BuildWavHeader(int sampleRate)
    {
        const short channels = 2;
        const short bits = 16;
        int byteRate = sampleRate * channels * bits / 8;
        short blockAlign = (short)(channels * bits / 8);

        using var ms = new MemoryStream(44);
        using var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(0xFFFFFFFFu);                 // unknown total size (streaming)
        w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);                          // fmt chunk size
        w.Write((short)1);                    // PCM
        w.Write(channels);
        w.Write(sampleRate);
        w.Write(byteRate);
        w.Write(blockAlign);
        w.Write(bits);
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(0xFFFFFFFFu);                 // unknown data size (streaming)
        w.Flush();
        return ms.ToArray();
    }

    public void Dispose()
    {
        try { _stream?.Dispose(); } catch { }
        try { _client?.Dispose(); } catch { }
        try { _listener.Stop(); } catch { }
    }
}
