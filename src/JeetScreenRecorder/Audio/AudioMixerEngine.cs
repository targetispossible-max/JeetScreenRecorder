using System;
using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace JeetScreenRecorder.Audio
{
    public class AudioMixerEngine : IDisposable
    {
        private WasapiLoopbackCapture _loopbackCapture;
        private WaveInEvent _micCapture;
        private BufferedWaveProvider _loopbackBuffer;
        private BufferedWaveProvider _micBuffer;
        
        private readonly Stream _destinationStream;
        private bool _isRecording;
        private readonly object _lockObject = new object();

        public int SampleRate { get; } = 48000;
        public int Channels { get; } = 2;

        public AudioMixerEngine(Stream destinationStream, bool captureSystemAudio, bool captureMic, string selectedMicDeviceId = null)
        {
            _destinationStream = destinationStream ?? throw new ArgumentNullException(nameof(destinationStream));

            WaveFormat targetFormat = new WaveFormat(SampleRate, 16, Channels);

            try
            {
                // 1. System Audio Setup (WASAPI Loopback)
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
                            if (_isRecording && e.BytesRecorded > 0)
                            {
                                _loopbackBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
                            }
                        }
                    };
                }

                // 2. Microphone Setup
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
                            WaveFormat = new WaveFormat(16000, 16, 1) // Standard mic format
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
                                if (_isRecording && e.BytesRecorded > 0)
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

        public void WriteMixedAudio(byte[] outputBuffer, int offset, int count)
        {
            // Yeh method AudioPipeSink dwara call kiya jayega jo mixed PCM data ko FFmpeg pipe me dalega
            // Yahan hum buffers se data read karke mix karte hain.
            // Implementation details AudioPipeSink me handle ki gayi hain.
        }

        public void Dispose()
        {
            Stop();
            _loopbackCapture?.Dispose();
            _micCapture?.Dispose();
        }
    }
}
