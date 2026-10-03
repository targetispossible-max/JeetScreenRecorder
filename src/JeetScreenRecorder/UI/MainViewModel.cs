using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using JeetScreenRecorder.Annotation;
using JeetScreenRecorder.Audio;
using JeetScreenRecorder.Capture;
using JeetScreenRecorder.Hotkeys;
using JeetScreenRecorder.Models;
using JeetScreenRecorder.Recording;
using JeetScreenRecorder.Settings;
using JeetScreenRecorder.Storage;
using JeetScreenRecorder.Utils;
using JeetScreenRecorder.VideoEncoding;

namespace JeetScreenRecorder.UI;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private sealed record Preset(string Name, int W, int H, int Fps, QualityPreset Q, bool? Mic = null, bool? Sys = null);

    private static readonly Preset[] Presets =
    {
        new("YouTube 1080p 60 FPS", 1920, 1080, 60, QualityPreset.High),
        new("YouTube 720p 60 FPS", 1280, 720, 60, QualityPreset.High),
        new("YouTube 1440p 60 FPS", 2560, 1440, 60, QualityPreset.High),
        new("YouTube 4K 60 FPS", 3840, 2160, 60, QualityPreset.VeryHigh),
        new("Tutorial Recording", 1920, 1080, 60, QualityPreset.High, true, true),
        new("Gaming Recording", 0, 0, 60, QualityPreset.Medium),
        new("Low-End PC", 1280, 720, 30, QualityPreset.Low)
    };

    private readonly IRecordingService _rec;
    private readonly ISettingsService _settings;
    private readonly IAudioCaptureService _audio;
    private readonly IScreenshotService _shot;
    private readonly IAnnotationService _annotation;
    private readonly IRegionSelector _regions;
    private readonly IWindowService _windows;
    private readonly IStorageService _storage;
    private readonly RecordingSettings _s;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private string _message = "";
    private string _estimate = "";
    private string _preset = "Custom";
    private int _countdown;
    private int _tick;
    private double _micLevel, _sysLevel;
    private WindowInfo? _selectedWindow;

    public RelayCommand StartCommand { get; }
    public RelayCommand PauseResumeCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand ScreenshotCommand { get; }
    public RelayCommand AnnotateCommand { get; }
    public RelayCommand SelectRegionCommand { get; }
    public RelayCommand RefreshWindowsCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand ChooseFolderCommand { get; }
    public RelayCommand TestAudioCommand { get; }

    public int[] FpsOptions { get; } = { 24, 30, 48, 50, 60 };
    public string[] ResolutionOptions { get; } =
        { "Original", "3840×2160", "2560×1440", "1920×1080", "1600×900", "1280×720", "854×480" };
    public string[] QualityOptions { get; } = { "Low", "Medium", "High", "Very High", "Lossless" };
    public string[] CaptureMethodOptions { get; } = { "Auto (GPU – fastest)", "Compatible (any screen)" };
    public string[] CountdownOptions { get; } = { "Off", "3 seconds", "5 seconds", "10 seconds" };
    public string[] CaptureAreaOptions { get; } = { "Full screen", "Custom region", "Application window" };
    public string[] PresetOptions { get; } = new[] { "Custom" }.Concat(Presets.Select(p => p.Name)).ToArray();
    public IReadOnlyList<MonitorInfo> MonitorOptions { get; }
    public IReadOnlyList<AudioDeviceInfo> MicOptions { get; }
    public ObservableCollection<WindowInfo> WindowOptions { get; } = new();

    public MainViewModel(IRecordingService rec, ISettingsService settings, IAudioCaptureService audio,
        IScreenshotService shot, IMonitorService monitors, IAnnotationService annotation,
        IRegionSelector regions, IWindowService windows, IStorageService storage)
    {
        _rec = rec;
        _settings = settings;
        _audio = audio;
        _shot = shot;
        _annotation = annotation;
        _regions = regions;
        _windows = windows;
        _storage = storage;
        _s = settings.Current;
        MonitorOptions = monitors.GetMonitors();
        MicOptions = audio.GetMicrophones();
        _audio.SetGains(_s.MicVolume, _s.SystemVolume);

        StartCommand = new RelayCommand(() => Run(StartWithCountdownAsync),
            () => _rec.State == RecordingState.Idle && _countdown == 0);
        PauseResumeCommand = new RelayCommand(
            () => Run(() => _rec.State == RecordingState.Paused ? _rec.ResumeAsync() : _rec.PauseAsync()),
            () => _rec.State is RecordingState.Recording or RecordingState.Paused);
        StopCommand = new RelayCommand(() => Run(_rec.StopAsync),
            () => _rec.State is RecordingState.Recording or RecordingState.Paused);
        ScreenshotCommand = new RelayCommand(() => Run(TakeScreenshotAsync));
        AnnotateCommand = new RelayCommand(() => _annotation.ToggleToolbar());
        SelectRegionCommand = new RelayCommand(SelectRegion, () => _rec.State == RecordingState.Idle);
        RefreshWindowsCommand = new RelayCommand(RefreshWindows);
        OpenFolderCommand = new RelayCommand(OpenFolder);
        ChooseFolderCommand = new RelayCommand(ChooseFolder, () => _rec.State == RecordingState.Idle);
        TestAudioCommand = new RelayCommand(ToggleTestAudio, () => _rec.State == RecordingState.Idle);

        _rec.StateChanged += (_, _) => Refresh();
        _rec.Notice += (_, msg) => Application.Current.Dispatcher.Invoke(() => Message = msg);
        _annotation.RecordToggleRequested += (_, _) => ToggleRecording();
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();

        if (_s.Source == CaptureSource.Window) RefreshWindows();
        UpdateEstimate();
        Run(_rec.InitializeAsync);
        Application.Current.Dispatcher.BeginInvoke(new Action(() => Run(CheckRecoveryAsync)), DispatcherPriority.ApplicationIdle);
    }

    // ---------------- helpers ----------------
    private async void Run(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex)
        {
            AppLogger.Error("Action failed", ex);
            Message = ex.Message;
            Refresh();
        }
    }

    private bool PrepareCaptureTarget()
    {
        var mon = SelectedMonitor;
        if (_s.Source == CaptureSource.CustomRegion)
        {
            if (mon == null || !CaptureOptionsFactory.IsValidRegion(_s, mon))
            {
                Message = "Please select a region first (click “Select region…”).";
                return false;
            }
        }
        else if (_s.Source == CaptureSource.Window)
        {
            var w = SelectedWindow;
            if (w == null)
            {
                Message = "Please choose an application window first.";
                return false;
            }
            var size = _windows.GetSize(w.Handle);
            if (size == null)
            {
                Message = "That window is no longer available (or is minimized). Click Refresh and choose it again.";
                RefreshWindows();
                return false;
            }
            _s.WindowHandle = w.Handle;
            _s.WindowWidth = size.Value.Width;
            _s.WindowHeight = size.Value.Height;
        }
        return true;
    }

    private async Task StartWithCountdownAsync()
    {
        if (!PrepareCaptureTarget()) return;
        PersistSettings();
        int n = _s.CountdownSeconds;
        if (n > 0)
        {
            try
            {
                for (int i = n; i > 0; i--)
                {
                    Countdown = i;
                    await Task.Delay(1000);
                }
            }
            finally { Countdown = 0; }
        }
        await _rec.StartAsync();
    }

    private async Task TakeScreenshotAsync()
    {
        if (!PrepareCaptureTarget()) return;
        var path = await _shot.CaptureAsync();
        Message = $"Screenshot saved: {path}";
    }

    private async Task CheckRecoveryAsync()
    {
        foreach (var dir in RecordingRecovery.FindIncomplete(_s.OutputFolder))
        {
            var answer = MessageBox.Show(
                $"An unfinished recording was found:\n{Path.GetFileName(dir).TrimStart('.')}\n\nRecover previous recording?",
                AppInfo.Name, MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) continue;
            try
            {
                Message = "Recovering previous recording…";
                var path = await RecordingRecovery.RecoverAsync(dir, _s.OutputFolder);
                Message = $"Recovered: {path}";
            }
            catch (Exception ex)
            {
                AppLogger.Error("Recovery failed", ex);
                Message = $"Recovery failed: {ex.Message}";
            }
        }
    }

    private void OnTick()
    {
        var (m, s) = _audio.ReadPeaks();
        _micLevel = Math.Max(Math.Sqrt(m) * 100, _micLevel * 0.85);
        _sysLevel = Math.Max(Math.Sqrt(s) * 100, _sysLevel * 0.85);
        OnPropertyChanged(nameof(MicLevel));
        OnPropertyChanged(nameof(SystemLevel));
        OnPropertyChanged(nameof(TimerText));
        OnPropertyChanged(nameof(StatsText));
        OnPropertyChanged(nameof(EncoderText));
        if (++_tick % 20 == 0) UpdateEstimate();
    }

    private void UpdateEstimate()
    {
        try
        {
            var mon = SelectedMonitor;
            if (mon == null) return;
            var o = CaptureOptionsFactory.Create(_s, mon);
            var (w, h) = FfmpegArgsBuilder.OutputSize(o);
            int kbps = _s.BitrateKbps > 0
                ? _s.BitrateKbps
                : (int)(SizeEstimator.RecommendedBitrateKbps(w, h, _s.Fps) * FfmpegArgsBuilder.QualityMultiplier(_s.Quality));
            int audioKbps = _s.MicEnabled || _s.SystemAudioEnabled ? _s.AudioBitrateKbps : 0;
            double gbPerHour = SizeEstimator.GbPerHour(kbps, audioKbps);
            double freeGb = _storage.GetFreeSpaceBytes(_s.OutputFolder) / 1073741824.0;
            _estimate = $"Estimated size: ~{gbPerHour:0.0} GB/hour  •  Free space: {freeGb:0.0} GB" +
                        (gbPerHour > 0 ? $"  (≈ {freeGb / gbPerHour:0.0} hours of recording)" : "");
        }
        catch { _estimate = ""; }
        OnPropertyChanged(nameof(EstimateText));
    }

    private void ToggleTestAudio()
    {
        if (_rec.State != RecordingState.Idle) return;
        if (_audio.IsRunning) _audio.Stop();
        else
        {
            _audio.SetGains(_s.MicVolume, _s.SystemVolume);
            _audio.Start(_s.MicDeviceId, _s.MicEnabled, _s.SystemAudioEnabled, _s.AudioSampleRate);
        }
        OnPropertyChanged(nameof(TestLabel));
    }

    private void SelectRegion()
    {
        var mon = SelectedMonitor;
        if (mon == null) return;
        RegionRect? initial = CaptureOptionsFactory.IsValidRegion(_s, mon)
            ? new RegionRect(_s.RegionX, _s.RegionY, _s.RegionWidth, _s.RegionHeight)
            : null;
        var r = _regions.Select(mon, initial);
        if (r == null) return;
        _s.RegionX = r.X;
        _s.RegionY = r.Y;
        _s.RegionWidth = r.Width;
        _s.RegionHeight = r.Height;
        PersistSettings();
        Message = "";
        OnPropertyChanged(nameof(RegionText));
    }

    private void RefreshWindows()
    {
        var keep = _selectedWindow?.Handle;
        WindowOptions.Clear();
        foreach (var w in _windows.GetWindows()) WindowOptions.Add(w);
        _selectedWindow = keep == null ? null : WindowOptions.FirstOrDefault(w => w.Handle == keep);
        OnPropertyChanged(nameof(SelectedWindow));
    }

    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(_s.OutputFolder);
            var last = _rec.LastOutputPath;
            var args = last != null && File.Exists(last) ? $"/select,\"{last}\"" : $"\"{_s.OutputFolder}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
        }
        catch (Exception ex) { Message = ex.Message; }
    }

    private void ChooseFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose where to save recordings",
            InitialDirectory = _s.OutputFolder
        };
        if (dlg.ShowDialog() == true)
        {
            _s.OutputFolder = dlg.FolderName;
            PersistSettings();
            OnPropertyChanged(nameof(OutputText));
        }
    }

    public void RegisterHotkeys(IHotkeyService hk)
    {
        var map = new (string Name, Action Action)[]
        {
            ("StartStop", ToggleRecording),
            ("PauseResume", () => TryExecute(PauseResumeCommand)),
            ("Screenshot", () => TryExecute(ScreenshotCommand)),
            ("ToggleToolbar", () => _annotation.ToggleToolbar())
        };
        foreach (var (name, action) in map)
        {
            if (!_s.Hotkeys.TryGetValue(name, out var gesture)) continue;
            if (!hk.Register(name, gesture, action))
                Message = $"Hotkey {gesture} is already used by another application.";
        }
    }

    private void ToggleRecording()
    {
        if (_rec.State == RecordingState.Idle) TryExecute(StartCommand);
        else TryExecute(StopCommand);
    }

    private static void TryExecute(RelayCommand c)
    {
        if (c.CanExecute(null)) c.Execute(null);
    }

    public bool IsBusy => _rec.State != RecordingState.Idle;

    public void PersistSettings()
    {
        try { _settings.Save(); }
        catch (Exception ex) { AppLogger.Error("Saving settings failed", ex); }
        UpdateEstimate();
    }

    public void Shutdown()
    {
        PersistSettings();
        _audio.Stop();
        _annotation.CloseAll();
    }

    // ---------------- bindable state ----------------
    public string Message
    {
        get => _message;
        set { _message = value; OnPropertyChanged(); }
    }

    public string EstimateText => _estimate;

    public int Countdown
    {
        get => _countdown;
        private set
        {
            _countdown = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
            StartCommand.Raise();
        }
    }

    public bool CanEditSettings => _rec.State == RecordingState.Idle && _countdown == 0;
    public string TimerText => _rec.Elapsed.ToString(@"hh\:mm\:ss");

    public string StatusText => _countdown > 0
        ? $"Recording starts in {_countdown}…"
        : _rec.State switch
        {
            RecordingState.Recording => "● Recording",
            RecordingState.Paused => "⏸ Paused",
            RecordingState.Finalizing => "Saving video…",
            _ => "Ready"
        };

    public string PauseLabel => _rec.State == RecordingState.Paused ? "▶ Resume" : "⏸ Pause";
    public string TestLabel => _audio.IsRunning && _rec.State == RecordingState.Idle ? "■ Stop audio test" : "🎧 Test audio levels";
    public string EncoderText => $"Encoder: {_rec.EncoderName}  •  Capture: {_rec.CaptureName}";
    public string OutputText => $"Saving to: {_s.OutputFolder}";

    public string StatsText
    {
        get
        {
            if (_rec.State == RecordingState.Idle || _rec.State == RecordingState.Finalizing)
                return "Choose your screen and settings, then press Start Recording (hotkey: F9).";
            var st = _rec.Stats;
            var text = $"{st.Resolution}  •  FPS: {st.Fps:0.0}  •  Size: {FormatSize(st.SizeBytes)}  •  Dropped: {st.Dropped}";
            if (_rec.State == RecordingState.Recording && _rec.Elapsed.TotalSeconds > 5 && st.Fps > 0 && st.Fps < _s.Fps * 0.9)
                text += "\n⚠ Performance issue detected. Try reducing resolution or encoder quality.";
            return text;
        }
    }

    private static string FormatSize(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / 1073741824.0:0.00} GB" : $"{bytes / 1048576.0:0.0} MB";

    public double MicLevel => _micLevel;
    public double SystemLevel => _sysLevel;

    // ---------------- capture area ----------------
    public string SelectedCaptureArea
    {
        get => _s.Source switch
        {
            CaptureSource.CustomRegion => CaptureAreaOptions[1],
            CaptureSource.Window => CaptureAreaOptions[2],
            _ => CaptureAreaOptions[0]
        };
        set
        {
            if (value == null) return;
            _s.Source = value == CaptureAreaOptions[1] ? CaptureSource.CustomRegion
                      : value == CaptureAreaOptions[2] ? CaptureSource.Window
                      : CaptureSource.FullScreen;
            if (_s.Source == CaptureSource.Window) RefreshWindows();
            PersistSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsRegionMode));
            OnPropertyChanged(nameof(IsWindowMode));
            OnPropertyChanged(nameof(RegionText));
        }
    }

    public bool IsRegionMode => _s.Source == CaptureSource.CustomRegion;
    public bool IsWindowMode => _s.Source == CaptureSource.Window;

    public string RegionText
    {
        get
        {
            var mon = SelectedMonitor;
            return mon != null && CaptureOptionsFactory.IsValidRegion(_s, mon)
                ? $"Region: {_s.RegionWidth}×{_s.RegionHeight} at ({_s.RegionX}, {_s.RegionY})"
                : "No region selected yet";
        }
    }

    public WindowInfo? SelectedWindow
    {
        get => _selectedWindow;
        set
        {
            if (value == null) return;
            _selectedWindow = value;
            OnPropertyChanged();
        }
    }

    // ---------------- settings ----------------
    public string SelectedPreset
    {
        get => _preset;
        set
        {
            if (value == null) return;
            _preset = value;
            if (value != "Custom") ApplyPreset(value);
            OnPropertyChanged();
        }
    }

    private void ApplyPreset(string name)
    {
        var p = Presets.FirstOrDefault(x => x.Name == name);
        if (p == null) return;
        _s.Width = p.W;
        _s.Height = p.H;
        _s.Fps = p.Fps;
        _s.Quality = p.Q;
        if (p.Mic is bool m) _s.MicEnabled = m;
        if (p.Sys is bool sy) _s.SystemAudioEnabled = sy;
        _audio.SetEnabled(_s.MicEnabled, _s.SystemAudioEnabled);
        PersistSettings();
        UpdateResolutionWarning();
        OnPropertyChanged(string.Empty);   // refresh every binding
    }

    private void MarkCustom()
    {
        if (_preset == "Custom") return;
        _preset = "Custom";
        OnPropertyChanged(nameof(SelectedPreset));
    }

    public int SelectedFps
    {
        get => _s.Fps;
        set { if (_s.Fps == value) return; _s.Fps = value; MarkCustom(); PersistSettings(); OnPropertyChanged(); }
    }

    public string SelectedResolution
    {
        get => _s.Width == 0 ? "Original" : $"{_s.Width}×{_s.Height}";
        set
        {
            if (value == null || value == SelectedResolution) return;
            if (value == "Original") { _s.Width = 0; _s.Height = 0; }
            else
            {
                var p = value.Split('×');
                _s.Width = int.Parse(p[0]);
                _s.Height = int.Parse(p[1]);
            }
            MarkCustom();
            PersistSettings();
            OnPropertyChanged();
            UpdateResolutionWarning();
        }
    }

    public string SelectedQuality
    {
        get => QualityOptions[Math.Clamp((int)_s.Quality, 0, QualityOptions.Length - 1)];
        set
        {
            int i = Array.IndexOf(QualityOptions, value);
            if (i < 0) return;
            _s.Quality = (QualityPreset)i;
            MarkCustom();
            PersistSettings();
            OnPropertyChanged();
        }
    }

    public string SelectedCaptureMethod
    {
        get => _s.CompatibleCapture ? CaptureMethodOptions[1] : CaptureMethodOptions[0];
        set
        {
            if (value == null) return;
            _s.CompatibleCapture = value == CaptureMethodOptions[1];
            PersistSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(EncoderText));
        }
    }

    public string SelectedCountdown
    {
        get => _s.CountdownSeconds switch { 3 => "3 seconds", 5 => "5 seconds", 10 => "10 seconds", _ => "Off" };
        set
        {
            _s.CountdownSeconds = value switch { "3 seconds" => 3, "5 seconds" => 5, "10 seconds" => 10, _ => 0 };
            PersistSettings();
            OnPropertyChanged();
        }
    }

    public MonitorInfo? SelectedMonitor
    {
        get => MonitorOptions.FirstOrDefault(m => m.Index == _s.MonitorIndex) ?? MonitorOptions.FirstOrDefault();
        set
        {
            if (value == null) return;
            _s.MonitorIndex = value.Index;
            PersistSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(RegionText));
            UpdateResolutionWarning();
        }
    }

    private void UpdateResolutionWarning()
    {
        var mon = SelectedMonitor;
        if (mon != null && _s.Width > 0 && (_s.Width > mon.Width || _s.Height > mon.Height))
            Message = $"This screen is {mon.Width}×{mon.Height}. The video will not be upscaled and will be recorded at the original size.";
        else if (Message.StartsWith("This screen is")) Message = "";
    }

    public bool HideFromCapture
    {
        get => _s.HideFromCapture;
        set { _s.HideFromCapture = value; PersistSettings(); OnPropertyChanged(); }
    }

    // ---------------- audio ----------------
    public string SelectedMicId
    {
        get => _s.MicDeviceId ?? "";
        set
        {
            _s.MicDeviceId = string.IsNullOrEmpty(value) ? null : value;
            _audio.SetMicDevice(_s.MicDeviceId);
            PersistSettings();
            OnPropertyChanged();
        }
    }

    public bool MicEnabled
    {
        get => _s.MicEnabled;
        set
        {
            _s.MicEnabled = value;
            _audio.SetEnabled(_s.MicEnabled, _s.SystemAudioEnabled);
            PersistSettings();
            OnPropertyChanged();
        }
    }

    public bool SystemEnabled
    {
        get => _s.SystemAudioEnabled;
        set
        {
            _s.SystemAudioEnabled = value;
            _audio.SetEnabled(_s.MicEnabled, _s.SystemAudioEnabled);
            PersistSettings();
            OnPropertyChanged();
        }
    }

    public double MicVolume
    {
        get => _s.MicVolume * 100;
        set
        {
            _s.MicVolume = Math.Clamp(value / 100, 0, 1);
            _audio.SetGains(_s.MicVolume, _s.SystemVolume);
            OnPropertyChanged();
        }
    }

    public double SystemVolume
    {
        get => _s.SystemVolume * 100;
        set
        {
            _s.SystemVolume = Math.Clamp(value / 100, 0, 1);
            _audio.SetGains(_s.MicVolume, _s.SystemVolume);
            OnPropertyChanged();
        }
    }

    private void Refresh()
    {
        var d = Application.Current?.Dispatcher;
        if (d != null && !d.CheckAccess())
        {
            d.Invoke(Refresh);
            return;
        }
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(TimerText));
        OnPropertyChanged(nameof(PauseLabel));
        OnPropertyChanged(nameof(TestLabel));
        OnPropertyChanged(nameof(CanEditSettings));
        OnPropertyChanged(nameof(StatsText));
        OnPropertyChanged(nameof(EncoderText));
        StartCommand.Raise();
        PauseResumeCommand.Raise();
        StopCommand.Raise();
        SelectRegionCommand.Raise();
        ChooseFolderCommand.Raise();
        TestAudioCommand.Raise();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
