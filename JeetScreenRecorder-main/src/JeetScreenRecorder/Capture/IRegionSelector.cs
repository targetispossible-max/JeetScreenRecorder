namespace JeetScreenRecorder.Capture;

/// <summary>Monitor-relative region in physical pixels.</summary>
public sealed record RegionRect(int X, int Y, int Width, int Height);

public interface IRegionSelector
{
    /// <summary>Shows the interactive selector. Returns null if cancelled.</summary>
    RegionRect? Select(MonitorInfo monitor, RegionRect? initial);
}
