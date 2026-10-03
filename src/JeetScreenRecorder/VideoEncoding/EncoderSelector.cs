using JeetScreenRecorder.Models;

namespace JeetScreenRecorder.VideoEncoding;

public static class EncoderSelector
{
    public static readonly EncoderInfo Software =
        new("libx264", "Software (x264)", VideoCodec.H264, false);

    public static EncoderInfo Choose(IReadOnlyList<EncoderInfo> available, VideoCodec codec, string preference)
    {
        if (!string.Equals(preference, "auto", StringComparison.OrdinalIgnoreCase))
        {
            var chosen = available.FirstOrDefault(e => e.Id == preference && e.Codec == codec);
            if (chosen != null) return chosen;
        }

        foreach (var suffix in new[] { "_nvenc", "_qsv", "_amf" })
        {
            var hw = available.FirstOrDefault(e =>
                e.Codec == codec && e.IsHardware && e.Id.EndsWith(suffix, StringComparison.Ordinal));
            if (hw != null) return hw;
        }
        return Software;
    }
}
