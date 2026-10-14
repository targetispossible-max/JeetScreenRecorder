using JeetScreenRecorder.Models;
using JeetScreenRecorder.Utils;

namespace JeetScreenRecorder.VideoEncoding;

/// <summary>Tests each hardware encoder with a real 5-frame capture+encode, so only working ones are offered.</summary>
public sealed class EncoderDetector : IEncoderDetector
{
    private static readonly EncoderInfo[] Candidates =
    {
        new("h264_nvenc", "NVIDIA NVENC (H.264)", VideoCodec.H264, true),
        new("hevc_nvenc", "NVIDIA NVENC (HEVC)", VideoCodec.Hevc, true),
        new("av1_nvenc",  "NVIDIA NVENC (AV1)",  VideoCodec.Av1,  true),
        new("h264_qsv",   "Intel Quick Sync (H.264)", VideoCodec.H264, true),
        new("hevc_qsv",   "Intel Quick Sync (HEVC)",  VideoCodec.Hevc, true),
        new("av1_qsv",    "Intel Quick Sync (AV1)",   VideoCodec.Av1,  true),
        new("h264_amf",   "AMD AMF (H.264)", VideoCodec.H264, true),
        new("hevc_amf",   "AMD AMF (HEVC)",  VideoCodec.Hevc, true),
        new("av1_amf",    "AMD AMF (AV1)",   VideoCodec.Av1,  true),
    };

    public async Task<IReadOnlyList<EncoderInfo>> DetectAsync()
    {
        var found = new List<EncoderInfo>();
        if (FfmpegLocator.Exists)
        {
            foreach (var c in Candidates)
            {
                try
                {
                    // 1) with the optional quality switches, 2) plain settings (older graphics cards / drivers)
                    if (await TestAsync(c, basic: false)) { found.Add(c); continue; }
                    if (await TestAsync(c, basic: true))
                    {
                        found.Add(c with { BasicOnly = true });
                        AppLogger.Info($"Encoder {c.Id} works only with basic settings");
                    }
                }
                catch (Exception ex) { AppLogger.Error($"Encoder test failed for {c.Id}", ex); }
            }
        }
        found.Add(EncoderSelector.Software);
        AppLogger.Info("Available encoders: " + string.Join(", ", found.Select(f => f.Id + (f.BasicOnly ? " (basic)" : ""))));
        return found;
    }

    private static async Task<bool> TestAsync(EncoderInfo c, bool basic)
    {
        var opts = new EncoderOptions { EncoderId = c.Id, Fps = 30, BitrateKbps = 4000, BasicEncoderArgs = basic };
        var (code, err) = await FfmpegRunner.RunAsync(FfmpegArgsBuilder.Build(opts, null, test: true), 20000);
        if (code == 0) return true;
        AppLogger.Info($"Encoder {c.Id} ({(basic ? "basic" : "best")}) not usable: {err.Trim().Split('\n').LastOrDefault()}");
        return false;
    }
}
