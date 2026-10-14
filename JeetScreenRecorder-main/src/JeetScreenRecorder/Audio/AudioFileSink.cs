namespace JeetScreenRecorder.Audio;

/// <summary>
/// Writes the mixed audio (raw 16-bit stereo PCM) to a file next to the video segment.
/// Audio never goes through ffmpeg while recording, so a problem with audio can no longer break the video,
/// and the audio of a crashed recording is still on disk.
/// </summary>
public sealed class AudioFileSink : IDisposable
{
    private readonly FileStream _fs;

    public string Path { get; }
    public Stream Stream => _fs;

    public AudioFileSink(string path)
    {
        Path = path;
        _fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
    }

    public void Dispose()
    {
        try { _fs.Flush(); } catch { }
        try { _fs.Dispose(); } catch { }
    }
}
