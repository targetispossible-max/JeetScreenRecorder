using System;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace JeetScreenRecorder.Audio
{
    public class AudioMixerEngine : IAudioCaptureService, IDisposable
    {
        private WasapiLoopbackCapture? _loopbackCapture;
        private WaveInEvent? _micCapture;
        private BufferedWaveProvider? _loopbackBuffer;
        private BufferedWaveProvider? _micBuffer;
        
        private bool _isRecording;
        private readonly object _lockObject = new object();

        public int SampleRate { get; } = 48000;
        public int Channels { get; } = 2;

        public AudioMixerEngine()
        {
            // Default constructor for DI
        }

        public void StartRecording(bool captureSystemAudio, bool captureMic, string? selectedMicDeviceId = null)
        {
            try
            {
                if (captureSystemAudio)
                {
                    _loopbackCapture = new WasapiLoopbackCapture();
                    _loopbackBuffer = new BufferedWaveProvider(_loopbackCapture.WaveFormat)
                    {
                        DiscardOnBufferOverflow = true,
                        BufferLength = 1024 * 1024
                    };

                    _loopbackCapture.DataAvailable += (s, e) =>
                    {
                        lock (_lockObject)
                        {
                            if (_isRecording && e.BytesRecorded > 0 && _loopbackBuffer != null)
                            {
                                _loopbackBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
                            }
                        }
                    };
                }

                if (captureMic)
                {
                    int deviceIndex = 0;
                    if (!string.IsNullOrEmpty(selectedMicDeviceId))
                    {
                        for (int i = 0; i < WaveIn.DeviceCount; i++)
                        {
                            var info = WaveIn.GetCapabilities(i);
                            if (info.ProductName.Contains(selectedMicDeviceId))
                            {
                                deviceIndex = i;
                                break;
                            }
                        }
                    }

                    if (WaveIn.DeviceCount > 0)
                    {
                        _micCapture = new WaveInEvent
                        {
                            DeviceNumber = deviceIndex,
                            WaveFormat = new WaveFormat(16000, 16, 1)
                        };

                        _micBuffer = new BufferedWaveProvider(_micCapture.WaveFormat)
                        {
                            DiscardOnBufferOverflow = true,
                            BufferLength = 1024 * 1024
                        };

                        _micCapture.DataAvailable += (s, e) =>
                        {
                            lock (_lockObject)
                            {
                                if (_isRecording && e.BytesRecorded > 0 && _micBuffer != null)
                                {
                                    _micBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
                                }
                            }
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Audio Initialization Error: {ex.Message}");
            }

            Start();
        }

        public void Start()
        {
            lock (_lockObject)
            {
                _isRecording = true;
                try { _loopbackCapture?.StartRecording(); } catch { }
                try { _micCapture?.StartRecording(); } catch { }
            }
        }

        public void Stop()
        {
            lock (_lockObject)
            {
                _isRecording = false;
                try { _loopbackCapture?.StopRecording(); } catch { }
                try { _micCapture?.StopRecording(); } catch { }
            }
        }

        public void Dispose()
        {
            Stop();
            _loopbackCapture?.Dispose();
            _micCapture?.Dispose();
        }
    }
}
