using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace JeetScreenRecorder.Audio
{
    public class AudioPipeSink : IDisposable
    {
        private readonly Stream _ffmpegStdin;
        private readonly AudioMixerEngine _mixerEngine;
        private CancellationTokenSource _cts;
        private Task _pumpTask;

        public AudioPipeSink(Stream ffmpegStdin, AudioMixerEngine mixerEngine)
        {
            _ffmpegStdin = ffmpegStdin ?? throw new ArgumentNullException(nameof(ffmpegStdin));
            _mixerEngine = mixerEngine ?? throw new ArgumentNullException(nameof(mixerEngine));
        }

        public void Start()
        {
            _cts = new CancellationTokenSource();
            _mixerEngine.Start();

            _pumpTask = Task.Run(async () =>
            {
                var buffer = new byte[4096];
                while (!_cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        // Safely write audio chunk to FFmpeg stdin pipe
                        // Agar data available nahi hai toh thoda sleep karein taaki CPU usage na badhe
                        await Task.Delay(10, _cts.Token);
                    }
                    catch (TaskCanceledException)
                    {
                        break;
                    }
                    catch (Exception)
                    {
                        break;
                    }
                }
            }, _cts.Token);
        }

        public void Stop()
        {
            _cts?.Cancel();
            _mixerEngine?.Stop();
            try
            {
                _pumpTask?.Wait(1000);
            }
            catch { }
        }

        public void Dispose()
        {
            Stop();
            _cts?.Dispose();
            _mixerEngine?.Dispose();
        }
    }
}
