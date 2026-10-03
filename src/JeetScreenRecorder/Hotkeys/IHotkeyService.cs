namespace JeetScreenRecorder.Hotkeys;

public interface IHotkeyService : IDisposable
{
    bool Register(string name, string gesture, Action callback);
    void Unregister(string name);
}
