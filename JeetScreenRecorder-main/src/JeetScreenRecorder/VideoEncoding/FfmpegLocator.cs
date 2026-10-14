namespace JeetScreenRecorder.VideoEncoding;

public static class FfmpegLocator
{
    public static string FfmpegPath
    {
        get
        {
            var dir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
            var p = Path.Combine(dir, "ffmpeg", "ffmpeg.exe");
            return File.Exists(p) ? p : Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
        }
    }

    public static bool Exists => File.Exists(FfmpegPath);
}
