using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using JeetScreenRecorder.Annotation;
using JeetScreenRecorder.Audio;
using JeetScreenRecorder.Capture;
using JeetScreenRecorder.Hotkeys;
using JeetScreenRecorder.Recording;
using JeetScreenRecorder.Settings;
using JeetScreenRecorder.Storage;
using JeetScreenRecorder.UI;
using JeetScreenRecorder.VideoEncoding;
using JeetScreenRecorder.Utils;
using JeetScreenRecorder.Webcam;

namespace JeetScreenRecorder;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            AppLogger.Error("Unhandled UI exception", args.Exception);
            MessageBox.Show("An unexpected error occurred. Details are in the log folder.",
                "Jeet Screen Recorder", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        var sc = new ServiceCollection();
        sc.AddSingleton<ISettingsService, JsonSettingsService>();
        sc.AddSingleton<IStorageService, StorageService>();
        sc.AddSingleton<IMonitorService, MonitorService>();
        sc.AddSingleton<IWindowService, WindowService>();
        sc.AddSingleton<IRegionSelector, RegionSelectorService>();
        sc.AddSingleton<AnnotationService>();
        sc.AddSingleton<IAnnotationService>(p => p.GetRequiredService<AnnotationService>());
        sc.AddSingleton<IScreenshotService, ScreenshotService>();
        sc.AddSingleton<IWebcamService, WebcamService>();
        sc.AddSingleton<IAudioCaptureService, AudioMixerEngine>();
        sc.AddSingleton<GlobalHotkeyService>();
        sc.AddSingleton<IHotkeyService>(p => p.GetRequiredService<GlobalHotkeyService>());
        sc.AddSingleton<IEncoderDetector, EncoderDetector>();
        sc.AddTransient<IVideoEncoder, FfmpegSegmentEncoder>();
        sc.AddSingleton<Func<IVideoEncoder>>(p => () => p.GetRequiredService<IVideoEncoder>());
        sc.AddSingleton<IRecordingService, RecordingService>();
        sc.AddSingleton<MainViewModel>();
        sc.AddTransient<MainWindow>();
        Services = sc.BuildServiceProvider();

        AppLogger.Info("Application started");
        Services.GetRequiredService<MainWindow>().Show();
    }
}
