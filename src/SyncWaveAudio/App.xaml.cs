using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SyncWaveAudio.Audio;
using SyncWaveAudio.Devices;
using SyncWaveAudio.Services;
using SyncWaveAudio.ViewModels;

namespace SyncWaveAudio;

public partial class App : Application
{
    public static IHost HostInstance { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        HostInstance = Host.CreateDefaultBuilder(e.Args)
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddDebug();
                logging.AddConsole();
                logging.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Debug);
            })
            .ConfigureServices(services =>
            {
                services.AddSingleton<AppSettings>();
                services.AddSingleton<IAudioDeviceService, WasapiAudioDeviceService>();
                services.AddSingleton<IBluetoothTelemetryService, BluetoothTelemetryService>();
                services.AddSingleton<IAudioSyncEngine, WasapiAudioSyncEngine>();
                services.AddSingleton<IProfileStore, JsonProfileStore>();
                services.AddSingleton<MainViewModel>();
            })
            .Build();

        base.OnStartup(e);
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (HostInstance.Services.GetService<IAudioSyncEngine>() is { } engine)
        {
            await engine.StopAsync();
        }

        HostInstance.Dispose();
        base.OnExit(e);
    }
}
