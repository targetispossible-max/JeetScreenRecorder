using System.Diagnostics;
using JeetScreenRecorder.Utils;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace JeetScreenRecorder.Audio;

/// <summary>
/// Captures microphone (WASAPI) and system audio (WASAPI loopback), mixes them on a wall-clock
/// timer (so silence never creates gaps => no drift) and writes 16-bit stereo PCM to a sink.
/// </summary>
public sealed class AudioMixerEngine : IAudioCaptureService
{
    private readonly object _lock = new();
    private readonly object _peakLock = new();

    private WasapiCapture? _mic;
    private WasapiLoopbackCapture? _sys;
    private ISampleProvider? _micSp, _sysSp;
    private Thread? _thread;
    private volatile bool _run;
    private volatile Stream? _sink;
    private volatile bool _micOn, _sysOn;
    private double _micGain = 1.0, _sysGain = 0.8;
    private double _micPeak, _sysPeak, _sinkPeak;
    private int _rate = 48000;
    private string? _micDeviceId;

    public event EventHandler<string>? Warning;
    public bool IsRunning => _run;

    public IReadOnlyList<AudioDeviceInfo> GetMicrophones()
    {
        var list = new List<AudioDeviceInfo> { new("", "Default microphone", AudioDeviceKind.Microphone) };
        try
        {
            using var en = new MMDeviceEnumerator();
            foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                list.Add(new AudioDeviceInfo(d.ID, d.FriendlyName, AudioDeviceKind.Microphone));
        }
        catch (Exception ex) { AppLogger.Error("Could not list microphones", ex); }
        return list;
    }

    public void Start(string? micDeviceId, bool mic, bool system, int sampleRate)
    {
        if (_run) return;
        _rate = sampleRate;
        _micDeviceId = micDeviceId;
        _micOn = mic;
        _sysOn = system;
        _run = true;
        using (var en = new MMDeviceEnumerator())
        {
            if (mic) StartMic(en);
            if (system) StartSystem(en);
        }
        _thread = new Thread(Pump) { IsBackground = true, Name = "AudioMixer", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        AppLogger.Info($"Audio engine started (mic={_micOn}, system={_sysOn}, {_rate} Hz)");
    }

    public void Stop()
    {
        if (!_run) return;
        _run = false;
        _sink = null;
        try { _thread?.Join(1500); } catch { }
        _thread = null;
        StopMic();
        StopSystem();
        AppLogger.Info("Audio engine stopped");
    }

    public void SetGains(double micGain, double systemGain)
    {
        _micGain = Math.Clamp(micGain, 0, 4);      // up to 400% (boost for quiet external microphones)
        _sysGain = Math.Clamp(systemGain, 0, 2);
    }

    public void SetEnabled(bool mic, bool system)
    {
        _micOn = mic;
        _sysOn = system;
        if (!_run) return;
        using var en = new MMDeviceEnumerator();
        if (mic && _mic == null) StartMic(en);
        if (!mic && _mic != null) StopMic();
        if (system && _sys == null) StartSystem(en);
        if (!system && _sys != null) StopSystem();
    }

    public void SetMicDevice(string? micDeviceId)
    {
        _micDeviceId = micDeviceId;
        if (!_run || _mic == null) return;
        StopMic();
        using var en = new MMDeviceEnumerator();
        StartMic(en);
    }

    public void SetSink(Stream? sink)
    {
        if (sink != null) lock (_peakLock) _sinkPeak = 0;
        _sink = sink;
    }

    public double SinkPeak { get { lock (_peakLock) return _sinkPeak; } }

    public (double Mic, double System) ReadPeaks()
    {
        lock (_peakLock)
        {
            var r = (_micPeak, _sysPeak);
            _micPeak = 0;
            _sysPeak = 0;
            return r;
        }
    }

    // ---------------- devices ----------------

    private void StartMic(MMDeviceEnumerator en)
    {
        try
        {
            var dev = string.IsNullOrEmpty(_micDeviceId)
                ? en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console)
                : en.GetDevice(_micDeviceId);
            var cap = new WasapiCapture(dev);
            var buf = NewBuffer(cap.WaveFormat);
            cap.DataAvailable += (_, e) => buf.AddSamples(e.Buffer, 0, e.BytesRecorded);
            cap.RecordingStopped += (_, e) =>
            {
                if (e.Exception != null)
                    Warning?.Invoke(this, "Microphone disconnected. Recording continues without microphone.");
            };
            cap.StartRecording();
            lock (_lock) { _mic = cap; _micSp = ToStereo(buf, _rate); }
            AppLogger.Info($"Microphone selected: {dev.FriendlyName}");
        }
        catch (Exception ex)
        {
            AppLogger.Error("Microphone start failed", ex);
            _micOn = false;
            Warning?.Invoke(this, "No working microphone was found. Recording without microphone.");
        }
    }

    private void StartSystem(MMDeviceEnumerator en)
    {
        try
        {
            var dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
            var cap = new WasapiLoopbackCapture(dev);
            var buf = NewBuffer(cap.WaveFormat);
            cap.DataAvailable += (_, e) => buf.AddSamples(e.Buffer, 0, e.BytesRecorded);
            cap.StartRecording();
            lock (_lock) { _sys = cap; _sysSp = ToStereo(buf, _rate); }
            AppLogger.Info($"System audio device: {dev.FriendlyName}");
        }
        catch (Exception ex)
        {
            AppLogger.Error("System audio start failed", ex);
            _sysOn = false;
            Warning?.Invoke(this, "System audio could not be captured on this device.");
        }
    }

    private void StopMic()
    {
        WasapiCapture? cap;
        lock (_lock) { cap = _mic; _mic = null; _micSp = null; }
        try { cap?.StopRecording(); cap?.Dispose(); } catch { }
    }

    private void StopSystem()
    {
        WasapiLoopbackCapture? cap;
        lock (_lock) { cap = _sys; _sys = null; _sysSp = null; }
        try { cap?.StopRecording(); cap?.Dispose(); } catch { }
    }

    private static BufferedWaveProvider NewBuffer(WaveFormat fmt) => new(fmt)
    {
        DiscardOnBufferOverflow = true,
        ReadFully = true,
        BufferDuration = TimeSpan.FromSeconds(2)
    };

    private static ISampleProvider ToStereo(BufferedWaveProvider buf, int rate)
    {
        ISampleProvider sp = buf.ToSampleProvider();
        int ch = sp.WaveFormat.Channels;
        if (ch == 1) sp = new MonoToStereoSampleProvider(sp);
        else if (ch > 2)
        {
            var mux = new MultiplexingSampleProvider(new[] { sp }, 2);
            mux.ConnectInputToOutput(0, 0);
            mux.ConnectInputToOutput(1, 1);
            sp = mux;
        }
        if (sp.WaveFormat.SampleRate != rate) sp = new WdlResamplingSampleProvider(sp, rate);
        return sp;
    }

    // ---------------- mixer clock ----------------

    private void Pump()
    {
        int maxFrames = _rate;
        var micBuf = new float[maxFrames * 2];
        var sysBuf = new float[maxFrames * 2];
        var pcm = new byte[maxFrames * 4];
        var sw = Stopwatch.StartNew();
        long produced = 0;

        while (_run)
        {
            long need = (long)(sw.Elapsed.TotalSeconds * _rate) - produced;
            if (need < _rate / 100) { Thread.Sleep(4); continue; }

            int frames = (int)Math.Min(need, maxFrames);
            int samples = frames * 2;
            Array.Clear(micBuf, 0, samples);
            Array.Clear(sysBuf, 0, samples);

            var msp = _micSp;
            var ssp = _sysSp;
            if (_micOn && msp != null) { try { msp.Read(micBuf, 0, samples); } catch { } }
            if (_sysOn && ssp != null) { try { ssp.Read(sysBuf, 0, samples); } catch { } }

            float mg = (float)_micGain, sg = (float)_sysGain, mPeak = 0, sPeak = 0, xPeak = 0;
            for (int i = 0; i < samples; i++)
            {
                float m = micBuf[i] * mg, s = sysBuf[i] * sg;
                float a = Math.Abs(m); if (a > mPeak) mPeak = a;
                a = Math.Abs(s); if (a > sPeak) sPeak = a;
                float v = m + s;
                // Soft limiter: loud peaks are rounded off smoothly instead of being cut hard (hard cut = crackling when boosted).
                float av = Math.Abs(v);
                if (av > 0.8f)
                {
                    float lim = 0.8f + 0.2f * MathF.Tanh((av - 0.8f) / 0.2f);
                    v = v < 0 ? -lim : lim;
                }
                a = Math.Abs(v); if (a > xPeak) xPeak = a;
                short sv = (short)(v * 32767f);
                pcm[i * 2] = (byte)(sv & 0xFF);
                pcm[i * 2 + 1] = (byte)((sv >> 8) & 0xFF);
            }

            var sink = _sink;
            lock (_peakLock)
            {
                if (mPeak > _micPeak) _micPeak = mPeak;
                if (sPeak > _sysPeak) _sysPeak = sPeak;
                if (sink != null && xPeak > _sinkPeak) _sinkPeak = xPeak;
            }

            if (sink != null)
            {
                try { sink.Write(pcm, 0, samples * 2); }
                catch (Exception ex) { AppLogger.Warn($"Audio sink closed: {ex.Message}"); _sink = null; }
            }
            produced += frames;
        }
    }

    public void Dispose() => Stop();
}
