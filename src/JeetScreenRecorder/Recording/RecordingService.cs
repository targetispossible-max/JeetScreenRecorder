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
/// Real recording pipeline: ffmpeg (ddagrab/gdigrab + hardware encoder) writes crash-safe video-only MKV segments,
/// while the app writes the mixed microphone + system audio to a PCM file next to each segment.
/// When a segment ends, video and audio are joined (video is copied, audio becomes AAC).
/// Pause = close current segment, Resume = new segment, Stop = join all segments into ONE file.
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
    private readonly object _attachLock = new();
    private AudioFileSink? _audioFile;      // audio file of the current segment
    private bool _audioPending;             // waiting for the first video frame before audio starts
    private double _audioDelay;             // seconds of silence to put in front of the audio
    private int _curIndex;                  // 1-based number of the current segment
    private string _audioNote = "";
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
            _audioNote = "";
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
        int index = _segments.Count + 1;
        var path = SegmentMuxer.VideoPath(_partsDir, index);

        // Tries the best settings first. If the PC cannot do something (camera, GPU capture, hardware encoder),
        // step by step simpler settings are used, so recording works on every Windows PC.
        while (true)
        {
            var encoder = _encoderFactory();
            encoder.StatsUpdated += OnStats;
            AudioFileSink? file = null;
            try
            {
                if (_useAudio) file = new AudioFileSink(SegmentMuxer.PcmPath(_partsDir, index));
                // The audio file is connected to the mixer only when the first video frame has been written
                // (see OnStats), so that audio and video start at the same moment.
                lock (_attachLock) { _curIndex = index; _audioFile = file; _audioPending = file != null; _audioDelay = 0; }
                await encoder.StartAsync(_opts, path);
                _enc = encoder;
                _segments.Add(path);
                return;
            }
            catch (EncoderStartException ex)
            {
                AppLogger.Error("Encoder failed to start", ex);
                DropAudioFile(file);
                await encoder.DisposeAsync();
                if (!TryDowngrade(ex)) throw;
            }
            catch
            {
                DropAudioFile(file);
                await encoder.DisposeAsync();
                throw;
            }
        }
    }

    private static bool LooksLikeCameraError(EncoderStartException ex)
    {
        var t = ex.Details.Length > 0 ? ex.Details : ex.Message;
        return t.Contains("dshow", StringComparison.OrdinalIgnoreCase)
            || t.Contains("video=", StringComparison.OrdinalIgnoreCase)
            || t.Contains("Could not run graph", StringComparison.OrdinalIgnoreCase);
    }

    private void DropWebcam()
    {
        _opts = _opts with { WebcamName = null };
        Notice?.Invoke(this, "The webcam could not be started (another app may be using it, or camera access is off in Windows Privacy settings). Recording continues without the webcam.");
    }

    /// <summary>Switches to the next simpler setting. Returns false when nothing simpler is left.</summary>
    private bool TryDowngrade(EncoderStartException ex)
    {
        bool cam = FfmpegArgsBuilder.HasWebcam(_opts);
        bool hw = FfmpegArgsBuilder.IsHardware(_opts.EncoderId);

        if (cam && LooksLikeCameraError(ex))
        {
            if (!_opts.WebcamAutoFormat)
            {
                _opts = _opts with { WebcamAutoFormat = true };   // camera may not offer 1280x720 @ 30
                AppLogger.Warn("Camera: retrying with the camera's own format");
            }
            else DropWebcam();
            return true;
        }
        // GPU frames straight into NVENC fail e.g. on laptops with two graphics cards -> copy frames through the CPU
        if (!cam && FfmpegArgsBuilder.IsNvenc(_opts.EncoderId) && !_opts.ForceCpuFrames
            && _opts.Backend == CaptureBackend.DesktopDuplication && _opts.WindowHandle == 0)
        {
            _opts = _opts with { ForceCpuFrames = true };
            _settings.Current.DisableZeroCopy = true;
            try { _settings.Save(); } catch { }
            AppLogger.Warn("GPU zero-copy failed; using the compatible frame path from now on");
            return true;
        }
        if (hw && !_opts.BasicEncoderArgs)
        {
            _opts = _opts with { BasicEncoderArgs = true };
            AppLogger.Warn("Hardware encoder: retrying with basic settings");
            return true;
        }
        if (_opts.Backend == CaptureBackend.DesktopDuplication && _opts.WindowHandle == 0)
        {
            _opts = _opts with { Backend = CaptureBackend.Gdigrab };
            Notice?.Invoke(this, "GPU screen capture is not available on this display. Switched to compatible capture.");
            return true;
        }
        if (_opts.EncoderId != "libx264")
        {
            _opts = _opts with { EncoderId = "libx264", BasicEncoderArgs = false };
            _encName = "Software (x264)";
            Notice?.Invoke(this, "Your selected hardware encoder is unavailable. The application has switched to software encoding.");
            return true;
        }
        if (cam)
        {
            DropWebcam();   // last resort: the cause was not recognised, so try without the camera
            return true;
        }
        return false;
    }

    private void DropAudioFile(AudioFileSink? file)
    {
        _audio.SetSink(null);
        lock (_attachLock) { _audioFile = null; _audioPending = false; }
        if (file == null) return;
        file.Dispose();
        try { File.Delete(file.Path); } catch { }
    }

    private void OnStats(object? sender, EncoderStats e)
    {
        _cur = e;
        Stats = new RecordingStats(e.Fps, _finishedBytes + e.SizeBytes, _droppedBase + e.DroppedFrames, _resolution);
        if (_audioPending && e.Frames > 0) AttachAudio(e.OutTimeSeconds);
    }

    /// <summary>Called when the first video frame exists: from now on the mixer writes audio into the segment's file.</summary>
    private void AttachAudio(double videoSeconds)
    {
        AudioFileSink? file;
        int index;
        double delay;
        lock (_attachLock)
        {
            if (!_audioPending || _audioFile == null) return;
            _audioPending = false;
            file = _audioFile;
            index = _curIndex;
            // Video time already recorded when audio starts + the encoder's own delay (software encoders buffer more frames)
            // + the optional manual correction from the settings file.
            double encoderLag = _opts.EncoderId == "libx264" ? 0.15 : 0.05;
            delay = Math.Max(0, videoSeconds + encoderLag + _settings.Current.AudioSyncOffsetMs / 1000.0);
            _audioDelay = delay;
        }
        SegmentMuxer.WriteInfo(_partsDir, index, delay, _opts.AudioSampleRate);
        _audio.SetSink(file!.Stream);
        AppLogger.Info($"Audio started for segment {index}; audio delay {delay:0.000}s");
    }

    private async Task StopCurrentSegmentAsync()
    {
        _audio.SetSink(null);
        double peak = _audio.SinkPeak;
        AudioFileSink? file;
        int index;
        double delay;
        lock (_attachLock)
        {
            file = _audioFile; _audioFile = null; _audioPending = false;
            index = _curIndex; delay = _audioDelay;
        }

        var enc = _enc;
        _enc = null;
        if (enc != null)
        {
            await enc.StopAsync();
            await enc.DisposeAsync();
            _droppedBase += _cur.DroppedFrames;
            _cur = new EncoderStats(0, 0, 0, 0);
        }

        if (file != null)
        {
            await Task.Delay(60);      // let the mixer finish a write that was already in progress
            file.Dispose();
        }

        if (enc == null || _segments.Count == 0) return;

        // Join this segment's video and audio into one file.
        var joined = await SegmentMuxer.MuxAsync(_partsDir, index, delay, _opts.AudioSampleRate, _opts.AudioBitrateKbps, _useAudio);
        _segments[^1] = joined;
        try { _finishedBytes += new FileInfo(joined).Length; } catch { }

        if (_useAudio)
        {
            AppLogger.Info($"Segment {index}: loudest audio level {peak:0.000}");
            if (peak < 0.002 && _audioNote.Length == 0)
            {
                _audioNote = "No sound was captured in this recording. Check that the right microphone is selected, " +
                             "Windows microphone privacy access is on, and that something was playing for system audio.";
                AppLogger.Warn(_audioNote);
            }
        }
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
                Notice?.Invoke(this, $"Saved: {path}" + (_stopReason.Length > 0 ? "\n" + _stopReason : "") + (_audioNote.Length > 0 ? "\n" + _audioNote : ""));
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
            AudioBitrateKbps = s.AudioBitrateKbps,
            BasicEncoderArgs = enc.BasicOnly,
            X264Preset = SoftwarePreset()
        };
    }

    /// <summary>Weak PCs get a faster x264 preset so recording stays smooth; strong PCs get a better-looking one.</summary>
    private static string SoftwarePreset() => Environment.ProcessorCount switch
    {
        <= 4 => "superfast",
        >= 12 => "faster",
        _ => "veryfast"
    };

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
