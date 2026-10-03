namespace JeetScreenRecorder.Capture;

public interface IMonitorService
{
    IReadOnlyList<MonitorInfo> GetMonitors();
    MonitorInfo Get(int index);
}
