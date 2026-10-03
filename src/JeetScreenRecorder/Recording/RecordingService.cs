using System.Diagnostics;
using JeetScreenRecorder.Audio;
using JeetScreenRecorder.Capture;
using JeetScreenRecorder.Models;
using JeetScreenRecorder.Settings;
using JeetScreenRecorder.Storage;
using JeetScreenRecorder.Utils;
using JeetScreenRecorder.VideoEncoding;

namespace JeetScreenRecorder.Recording;

/// <summary>
/// Real recording pipeline: ffmpeg (ddagrab/gdigrab + hardware encoder + mixed audio pipe) writes crash-safe
/// MKV segments. Pause = close current segment, Resume = new segment, Stop = join all into ONE file.
/// </summary>
public sealed class RecordingService : IRecordingService
{
    private readonly ISettingsService _settings;
    private readonly IStorageService _storage;
    private readonly IEncoderDetector _detector;
    private readonly IMonitorService _monitors;
    private readonly IAudioCaptureService _audio;
    private readonly Func<IVideoEncoder> _encoderFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Stopwatch _clock = new();
    private readonly List<string> _segments = new();

    private Task<IReadOnlyList<EncoderInfo>>? _detectTask;
    private IReadOnlyList<EncoderInfo>? _available;
    private IVideoEncoder? _enc;
    private AudioPipeSink? _sink;
    private EncoderOptions _opts = new();
    private bool _useAudio;
    private string _encName = "";
    private string _resolution = "";
    private string _partsDir = "", _finalPath = "";
    private long _finishedBytes, _droppedBase;
    private EncoderStats _cur = new(0, 0, 0, 0);
    private Timer? _diskTimer;
    private bool _diskWarned;
    private string _stopReason = "";

    public RecordingService(ISettingsService settings, IStorageService storage, IEncoderDetector detector,
        IMonitorService monitors, IAudioCaptureService audio, Func<IVideoEncoder> encoderFactory)
    {
        _settings = settings;
        _storage = storage;
        _detector = detector;
        _monitors = monitors;
        _audio = audio;
        _encoderFactory = encoderFactory;
        _audio.Warning += (_, msg) => Notice?.Invoke(this, msg);
    }

    public RecordingState State { get; private set; } = RecordingState.Idle;
    public TimeSpan Elapsed => _clock.Elapsed;
    public RecordingStats Stats { get; private set; } = RecordingStats.Empty;
    public string? LastOutputPath { get; private set; }
    public event EventHandler? StateChanged;
    public event EventHandler<string>? Notice;

    public string EncoderName =>
        State != RecordingState.Idle && _encName.Length > 0 ? _encName
        : _available == null ? "detecting…"
        : EncoderSelector.Choose(_available, _settings.Current.Codec, _settings.Current.Encoder).DisplayName;

    public string CaptureName
    {
        get
        {
            bool gdi = State != RecordingState.Idle ? _opts.Backend == CaptureBackend.Gdigrab : _settings.Current.CompatibleCapture;
            return gdi ? "Compatible (GDI)" : "GPU (DXGI)";
        }
    }

    public async Task InitializeAsync() => await EnsureEncodersAsync();

    private async Task EnsureEncodersAsync()
    {
        _detectTask ??= _detector.DetectAsync();
        _available = await _detectTask;
    }

    public async Task StartAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (State != RecordingState.Idle) return;
            var s = _settings.Current;

            if (!FfmpegLocator.Exists)
                throw new InvalidOperationException("FFmpeg was not found next to the application. Please reinstall Jeet Screen Recorder.");
            try { Directory.CreateDirectory(s.OutputFolder); }
            catch (Exception ex) { throw new InvalidOperationException($"Cannot use the output folder '{s.OutputFolder}': {ex.Message}"); }
            if (!_storage.HasEnoughSpace(s.OutputFolder, s.MinFreeDiskMb * 1024L * 1024L))
                throw new InvalidOperationException("Not enough free disk space to start recording. Free some space or change the output folder.");

            await EnsureEncodersAsync();
            var enc = EncoderSelector.Choose(_available!, s.Codec, s.Encoder);
            _opts = BuildOptions(s, enc);
            _encName = enc.DisplayName;
            var (w, h) = FfmpegArgsBuilder.OutputSize(_opts);
            _resolution = $"{w}×{h}";

            var ext = s.Container switch
            {
                ContainerFormat.Mp4 => "mp4",
                ContainerFormat.Mov => "mov",
                _ => s.AutoRemuxToMp4 ? "mp4" : "mkv"
            };
            _finalPath = Path.Combine(s.OutputFolder, OutputNaming.Generate(s.FilePrefix, ext, DateTime.Now));
            _partsDir = Path.Combine(s.OutputFolder, "." + Path.GetFileNameWithoutExtension(_finalPath) + "_parts");
            Directory.CreateDirectory(_partsDir);

            _segments.Clear();
            _finishedBytes = 0;
            _droppedBase = 0;
            _cur = new EncoderStats(0, 0, 0, 0);
            Stats = new RecordingStats(0, 0, 0, _resolution);

            _useAudio = s.MicEnabled || s.SystemAudioEnabled;
            if (_useAudio)
            {
                _audio.SetGains(s.MicVolume, s.SystemVolume);
                if (_audio.IsRunning)
                {
                    _audio.SetMicDevice(s.MicDeviceId);
                    _audio.SetEnabled(s.MicEnabled, s.SystemAudioEnabled);
                }
                else _audio.Start(s.MicDeviceId, s.MicEnabled, s.SystemAudioEnabled, s.AudioSampleRate);
            }

            AppLogger.Info($"Encoder selected: {enc.Id}; capture {_resolution} @ {_opts.Fps} FPS; bitrate {_opts.BitrateKbps} kbps; " +
                           $"monitor {_opts.MonitorIndex} ({_opts.Backend}); audio={_useAudio}");
            try { await StartSegmentAsync(); }
            catch
            {
                if (_useAudio) _audio.Stop();
                try { Directory.Delete(_partsDir, true); } catch { }
                throw;
            }

            _clock.Restart();
            Set(RecordingState.Recording);
            AppLogger.Info("Recording started");
            _diskWarned = false;
            _stopReason = "";
            _diskTimer?.Dispose();
            _diskTimer = new Timer(_ => CheckDisk(), null, 5000, 5000);
        }
        finally { _gate.Release(); }
    }

    private async Task StartSegmentAsync()
    {
        var path = Path.Combine(_partsDir, $"part{_segments.Count + 1:000}.mkv");
        var encoder = _encoderFactory();
        encoder.StatsUpdated += OnStats;
        AudioPipeSink? sink = null;
        try
        {
            var opts = _opts;
            Task? connect = null;
            if (_useAudio)
            {
                sink = new AudioPipeSink();
                opts = opts with { AudioPipePath = sink.FfmpegPath };
                connect = sink.ConnectAsync(TimeSpan.FromSeconds(8));
            }
            await encoder.StartAsync(opts, path);
            if (connect != null)
            {
                await connect;
                _audio.SetSink(sink!.Stream);
            }
        }
        catch (EncoderStartException ex)
        {
            AppLogger.Error("Encoder failed to start", ex);
            _audio.SetSink(null);
            sink?.Dispose();
            await encoder.DisposeAsync();

            if (_opts.Backend == CaptureBackend.DesktopDuplication)
            {
                _opts = _opts with { Backend = CaptureBackend.Gdigrab };
                Notice?.Invoke(this, "GPU screen capture is not available on this display. Switched to compatible capture.");
                await StartSegmentAsync();
                return;
            }
            if (_opts.EncoderId != "libx264")
            {
                _opts = _opts with { EncoderId = "libx264" };
                _encName = "Software (x264)";
                Notice?.Invoke(this, "Your selected hardware encoder is unavailable. The application has switched to software encoding.");
                await StartSegmentAsync();
                return;
            }
            throw;
        }
        catch (OperationCanceledException)
        {
            _audio.SetSink(null);
            sink?.Dispose();
            await encoder.DisposeAsync();
            throw new InvalidOperationException("Audio could not be connected to the recorder. Try again or turn audio off.");
        }
        catch
        {
            _audio.SetSink(null);
            sink?.Dispose();
            await encoder.DisposeAsync();
            throw;
        }
        _enc = encoder;
        _sink = sink;
        _segments.Add(path);
    }

    private void OnStats(object? sender, EncoderStats e)
    {
        _cur = e;
        Stats = new RecordingStats(e.Fps, _finishedBytes + e.SizeBytes, _droppedBase + e.DroppedFrames, _resolution);
    }

    private async Task StopCurrentSegmentAsync()
    {
        _audio.SetSink(null);
        var enc = _enc;
        var sink = _sink;
        _enc = null;
        _sink = null;
        if (enc != null)
        {
            await enc.StopAsync();
            await enc.DisposeAsync();
            try { _finishedBytes += new FileInfo(_segments[^1]).Length; } catch { }
            _droppedBase += _cur.DroppedFrames;
            _cur = new EncoderStats(0, 0, 0, 0);
        }
        sink?.Dispose();
    }

    public async Task PauseAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (State != RecordingState.Recording) return;
            _clock.Stop();
            await StopCurrentSegmentAsync();
            Set(RecordingState.Paused);
            AppLogger.Info("Recording paused");
        }
        finally { _gate.Release(); }
    }

    public async Task ResumeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (State != RecordingState.Paused) return;
            await StartSegmentAsync();
            _clock.Start();
            Set(RecordingState.Recording);
            AppLogger.Info("Recording resumed");
        }
        finally { _gate.Release(); }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (State == RecordingState.Idle || State == RecordingState.Finalizing) return;
            _diskTimer?.Dispose();
            _diskTimer = null;
            _clock.Stop();
            Set(RecordingState.Finalizing);
            try
            {
                await StopCurrentSegmentAsync();
                _audio.Stop();
                var path = await RecordingFinalizer.FinalizeAsync(_segments.ToList(), _finalPath, _partsDir);
                LastOutputPath = path;
                AppLogger.Info($"Recording stopped after {_clock.Elapsed}; saved {path}");
                Notice?.Invoke(this, $"Saved: {path}" + (_stopReason.Length > 0 ? "\n" + _stopReason : ""));
            }
            catch (Exception ex)
            {
                AppLogger.Error("Finalizing recording failed", ex);
                Notice?.Invoke(this, $"Could not finish the video file: {ex.Message} The raw parts are kept in: {_partsDir}");
            }
            finally { _stopReason = ""; Set(RecordingState.Idle); }
        }
        finally { _gate.Release(); }
    }

    private EncoderOptions BuildOptions(RecordingSettings s, EncoderInfo enc)
    {
        var mon = _monitors.Get(s.MonitorIndex);
        var probe = CaptureOptionsFactory.Create(s, mon);
        var (w, h) = FfmpegArgsBuilder.OutputSize(probe);
        int kbps = s.BitrateKbps > 0
            ? s.BitrateKbps
            : (int)(SizeEstimator.RecommendedBitrateKbps(w, h, s.Fps) * FfmpegArgsBuilder.QualityMultiplier(s.Quality));
        return probe with
        {
            EncoderId = enc.Id,
            BitrateKbps = kbps,
            AudioSampleRate = s.AudioSampleRate,
            AudioBitrateKbps = s.AudioBitrateKbps
        };
    }

    private void CheckDisk()
    {
        try
        {
            if (State != RecordingState.Recording && State != RecordingState.Paused) return;
            var s = _settings.Current;
            long free = _storage.GetFreeSpaceBytes(s.OutputFolder);
            long min = s.MinFreeDiskMb * 1024L * 1024L;
            if (free < min)
            {
                _stopReason = $"Recording was stopped automatically because free disk space fell below {s.MinFreeDiskMb} MB.";
                AppLogger.Warn(_stopReason);
                _ = Task.Run(StopAsync);
            }
            else if (free < min * 2 && !_diskWarned)
            {
                _diskWarned = true;
                Notice?.Invoke(this, "Warning: disk space is running low.");
            }
        }
        catch (Exception ex) { AppLogger.Warn($"Disk check failed: {ex.Message}"); }
    }

    private void Set(RecordingState s)
    {
        State = s;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
