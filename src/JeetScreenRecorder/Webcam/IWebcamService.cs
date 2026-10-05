namespace JeetScreenRecorder.Webcam;

public sealed record CameraInfo(string Id, string Name);

public interface IWebcamService
{
    /// <summary>Lists the cameras that Windows (DirectShow) offers.</summary>
    Task<IReadOnlyList<CameraInfo>> DetectAsync();

    /// <summary>Takes one picture with the camera. Returns the picture file, or null (with the reason) when it failed.</summary>
    Task<(string? Path, string Error)> TakeSnapshotAsync(string cameraName);
}
