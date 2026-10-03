namespace JeetScreenRecorder.Webcam;

public sealed record CameraInfo(string Id, string Name);

public interface IWebcamService
{
    IReadOnlyList<CameraInfo> GetCameras();
    Task StartAsync(string cameraId, int width, int height, int fps);
    Task StopAsync();
}
