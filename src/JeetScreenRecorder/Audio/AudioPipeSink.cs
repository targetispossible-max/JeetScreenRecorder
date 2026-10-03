using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace JeetScreenRecorder.Audio
{
    public class AudioPipeSink : IDisposable
    {
        private readonly MemoryStream _memoryStream;
        private CancellationTokenSource? _cts;
        private Task? _pumpTask;

        public string FfmpegPath { get; set; } = string.Empty;
        public Stream Stream => _memoryStream;

        public AudioPipeSink()
        {
            _memoryStream = new MemoryStream();
        }

        public AudioPipeSink(Stream destinationStream, AudioMixerEngine? mixerEngine = null)
        {
            _memoryStream = destinationStream as MemoryStream ?? new MemoryStream();
        }

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            
            _pumpTask = Task.Run(async () =>
            {
                while (_cts != null && !_cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(10, _cts.Token);
                    }
                    catch (TaskCanceledException)
                    {
                        break;
                    }
                    catch
                    {
                        break;
                    }
                }
            }, _cts.Token);

            return Task.CompletedTask;
        }

        public void Start()
        {
            // Compatibility method
        }

        public void Stop()
        {
            _cts?.Cancel();
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
            _memoryStream?.Dispose();
        }
    }
}
