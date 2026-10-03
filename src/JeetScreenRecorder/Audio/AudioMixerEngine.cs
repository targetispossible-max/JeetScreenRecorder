using System;
using System.Collections.Generic;
using System.IO;
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
        private Stream? _destinationStream;

        private bool _captureSystem = true;
        private bool _captureMic = false;
        private string? _selectedMicDevice;
        private double _systemGain = 1.0;
        private double _micGain = 1.0;

        private readonly object _lockObject = new object();

        public bool IsRunning { get; private set; }
        public event EventHandler<string>? Warning;

        public int SampleRate { get; private set; } = 48000;
        public int Channels { get; } = 2;

        public AudioMixerEngine()
        {
        }

        public IReadOnlyList<AudioDeviceInfo> GetMicrophones()
        {
            var mics = new List<AudioDeviceInfo>();
            try
            {
                for (int i = 0; i < WaveIn.DeviceCount; i++)
                {
                    var caps = WaveIn.GetCapabilities(i);
                    mics.Add(new AudioDeviceInfo(caps.ProductName, i.ToString()));
                }
            }
            catch (Exception ex)
            {
                Warning?.Invoke(this, $"Error getting microphones: {ex.Message}");
            }
            return mics;
        }

        public void SetSink(Stream? stream)
        {
            _destinationStream = stream;
        }

        public void SetMicDevice(string? deviceId)
        {
            _selectedMicDevice = deviceId;
        }

        public void SetEnabled(bool systemAudio, bool mic)
        {
            _captureSystem = systemAudio;
            _captureMic = mic;
        }

        public void SetGains(double systemGain, double micGain)
        {
            _systemGain = systemGain;
            _micGain = micGain;
        }

        // Interface compliance ke liye dono overloads provide kiye gaye hain
        public void ReadPeaks()
        {
        }

        public void ReadPeaks(out float systemPeak, out float micPeak)
        {
            systemPeak = 0.0f;
            micPeak = 0.0f;
        }

        public void Start(string? micDevice, bool captureSystem, bool captureMic, int sampleRate)
        {
            _selectedMicDevice = micDevice ?? _selectedMicDevice;
            _captureSystem = captureSystem;
            _captureMic = captureMic;
            SampleRate = sampleRate > 0 ? sampleRate : 48000;

            StartRecording();
        }

        public void StartRecording()
        {
            lock (_lockObject)
            {
                if (IsRunning) return;

                try
                {
                    if (_captureSystem)
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
                                if (IsRunning && e.BytesRecorded > 0 && _loopbackBuffer != null)
                                {
                                    _loopbackBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
                                    _destinationStream?.Write(e.Buffer, 0, e.BytesRecorded);
                                }
                            }
                        };
                        _loopbackCapture.StartRecording();
                    }

                    if (_captureMic)
                    {
                        int deviceIndex = 0;
                        if (!string.IsNullOrEmpty(_selectedMicDevice))
                        {
                            for (int i = 0; i < WaveIn.DeviceCount; i++)
                            {
                                var info = WaveIn.GetCapabilities(i);
                                if (info.ProductName.Contains(_selectedMicDevice))
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
                                    if (IsRunning && e.BytesRecorded > 0 && _micBuffer != null)
                                    {
                                        _micBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
                                        _destinationStream?.Write(e.Buffer, 0, e.BytesRecorded);
                                    }
                                }
                            };
                            _micCapture.StartRecording();
                        }
                    }

                    IsRunning = true;
                }
                catch (Exception ex)
                {
                    Warning?.Invoke(this, $"StartRecording Error: {ex.Message}");
                }
            }
        }

        public void Stop()
        {
            lock (_lockObject)
            {
                IsRunning = false;
                try { _loopbackCapture?.StopRecording(); } catch { }
                try { _micCapture?.StopRecording(); } catch { }
            }
        }

        public void Dispose()
        {
            Stop();
            _loopbackCapture?.Dispose();
            _micCapture?.Dispose();
            _destinationStream = null;
        }
    }
}
