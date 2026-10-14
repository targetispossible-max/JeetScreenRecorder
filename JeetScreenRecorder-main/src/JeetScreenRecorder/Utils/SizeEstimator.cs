namespace JeetScreenRecorder.Utils;

public static class SizeEstimator
{
    /// <summary>Estimated recording size in GB per hour (decimal GB).</summary>
    public static double GbPerHour(int videoKbps, int audioKbps) =>
        (videoKbps + audioKbps) * 1000.0 / 8.0 * 3600.0 / 1_000_000_000.0;

    /// <summary>Recommended H.264 bitrate (kbps) for the given resolution and fps.</summary>
    public static int RecommendedBitrateKbps(int width, int height, int fps)
    {
        long pixels = (long)width * height;
        int at60 = pixels switch
        {
            <= 854 * 480 => 4000,
            <= 1280 * 720 => 8000,
            <= 1920 * 1080 => 16000,
            <= 2560 * 1440 => 30000,
            _ => 60000
        };
        return (int)Math.Round(at60 * (fps / 60.0));
    }
}
