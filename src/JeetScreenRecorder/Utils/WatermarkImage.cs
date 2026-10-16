using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace JeetScreenRecorder.Utils;

/// <summary>Creates the watermark picture (logo + "Jeet Screen Recorder", semi-transparent) as a PNG for ffmpeg.</summary>
public static class WatermarkImage
{
    private static string PngPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JeetScreenRecorder", "mark.png");

    /// <summary>Returns the PNG path, or null if it could not be created (then the recording simply has no mark).</summary>
    public static string? Ensure()
    {
        try
        {
            if (File.Exists(PngPath) && new FileInfo(PngPath).Length > 0) return PngPath;
            Directory.CreateDirectory(Path.GetDirectoryName(PngPath)!);
            var disp = Application.Current?.Dispatcher;
            if (disp == null) return null;
            if (disp.CheckAccess()) Render(); else disp.Invoke(Render);
            return File.Exists(PngPath) ? PngPath : null;
        }
        catch (Exception ex) { AppLogger.Warn($"Could not create the watermark picture: {ex.Message}"); return null; }
    }

    private static void Render()
    {
        const int W = 640, H = 160;
        var logo = new BitmapImage(new Uri("pack://application:,,,/Assets/logo256.png", UriKind.Absolute));
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(110, 0, 0, 0)), null, new Rect(0, 0, W, H), 24, 24);
            dc.DrawImage(logo, new Rect(14, 14, 132, 132));
            var tf = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            var text = new FormattedText(AppInfo.Name, System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, tf, 54, Brushes.White, 1.0);
            text.MaxTextWidth = W - 170;
            text.MaxTextHeight = H - 20;
            dc.DrawText(text, new Point(160, (H - text.Height) / 2));
        }
        var rtb = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(PngPath);
        enc.Save(fs);
    }
}
