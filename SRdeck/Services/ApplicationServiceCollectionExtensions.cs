using Microsoft.Extensions.DependencyInjection;
using SRdeckPlugin.Contracts;
using SRdeck.Audio;
using SRdeck.Configuration;
using SRdeck.Models;
using SRdeck.Models.SDR;
using SRdeck.SDR;
using SRdeck.Services.Plugins;
using SRdeck.ViewModels;
using SRdeck.Views;

namespace SRdeck.Services;

internal static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddPluginServices(
        this IServiceCollection services,
        IReadOnlyList<IPluginModule> pluginModules) =>
        services
            .AddSingleton(TimeProvider.System)
            .AddSingleton(pluginModules)
            .AddSingleton<IPluginSettingsStoreFactory, JsonPluginSettingsStoreFactory>()
            .AddSingleton<IPluginTuningServiceFactory>(sp => new PluginTuningServiceFactory(
                () => sp.GetRequiredService<IPluginManager>(),
                sp.GetRequiredService<IRadioControlStore>(),
                sp.GetRequiredService<IRadioControlUpdatePublisher>()))
            .AddSingleton<PluginAudioRouter>(sp => new PluginAudioRouter(
                () => sp.GetRequiredService<IPluginManager>(),
                new WaveOutAudioOutput(),
                disposeAudioOutput: true))
            .AddSingleton<IPluginAudioSinkFactory>(sp => sp.GetRequiredService<PluginAudioRouter>())
            .AddSingleton<IPluginNotificationService, PluginNotificationService>()
            .AddSingleton<SRdeckPlugin.Contracts.IPluginDispatcher, WpfPluginDispatcher>()
            .AddSingleton<IPluginMetricsRegistry, PluginMetricsRegistry>()
            .AddSingleton<IPluginHostContextFactory>(sp => new PluginHostContextFactory(
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<IPluginSettingsStoreFactory>(),
                sp.GetRequiredService<IPluginTuningServiceFactory>(),
                sp.GetRequiredService<IPluginAudioSinkFactory>(),
                 sp.GetRequiredService<IPluginMetricsRegistry>(),
                 () => sp.GetRequiredService<IPluginIqDispatcher>(),
                 sp.GetRequiredService<IPluginNotificationService>(),
                 sp.GetRequiredService<SRdeckPlugin.Contracts.IPluginDispatcher>(),
                 sp.GetRequiredService<IRadioStateStore>()))
            .AddSingleton<IPluginManager>(sp => new PluginManager(
                sp.GetRequiredService<IReadOnlyList<IPluginModule>>(),
                sp.GetRequiredService<IPluginHostContextFactory>()))
            .AddSingleton<PluginCodeWarmupService>()
            .AddSingleton<NativeStandardChannelGpuBackend>()
            .AddSingleton<GpuChannelCalibrationService>()
            .AddSingleton<IPluginIqDispatcher>(sp => new PluginIqDispatcher(
                sp.GetRequiredService<IReadOnlyList<IPluginModule>>(),
                sp.GetRequiredService<IPluginManager>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<IPluginMetricsRegistry>(),
                sp.GetRequiredService<NativeStandardChannelGpuBackend>()))
            .AddSingleton<PluginWorkspaceViewModel>();

    public static IServiceCollection AddRadioServices(this IServiceCollection services) =>
        services
            .AddSingleton<ISdrEngine, CoreEngine>()
            .AddSingleton<ISdrDevicePropertySynchronizer, SdrDevicePropertySynchronizer>()
            .AddSingleton<ISdrFrequencyTransitionTracker, SdrFrequencyTransitionTracker>()
            .AddSingleton<IEffectiveSampleRateTracker, EffectiveSampleRateTracker>()
            .AddSingleton<IIqSampleExtremaCalculator, IqSampleExtremaCalculator>()
            .AddSingleton<ISignalInputMetrics, SignalInputMetrics>()
            .AddSingleton<ISdrDeviceBindingFactory, SdrDeviceBindingFactory>()
            .AddSingleton<ISdrDeviceManagerFactory, SdrDeviceManagerFactory>()
            .AddSingleton<ISignalBufferManager, SignalBufferManager>()
            .AddSingleton<ISignalBufferState, SignalBufferState>()
            .AddSingleton<ISignalBufferWriterFactory, SignalBufferWriterFactory>()
            .AddSingleton<ISignalBlockCoordinatorFactory, SignalBlockCoordinatorFactory>()
            .AddSingleton<ISignalPipelineFactory, SignalPipelineFactory>()
            .AddSingleton<IProcessingCycleCoordinator, ProcessingCycleCoordinator>()
            .AddSingleton<IRadioStateStore, RadioStateStore>()
            .AddSingleton<IRadioControlStore, RadioControlStore>()
            .AddSingleton<IRadioControlUpdatePublisher, RadioControlUpdatePublisher>()
            .AddSingleton<ITuningCoordinator, TuningCoordinator>()
            .AddSingleton<IRadioSessionEngine>(sp => sp.GetRequiredService<ISdrEngine>())
            .AddSingleton<IRadioSessionTransitionCoordinator, RadioSessionTransitionCoordinator>()
            .AddSingleton<ISdrSessionStarter, SdrSessionStarter>()
            .AddSingleton<IPlaybackSessionStarter, PlaybackSessionStarter>()
            .AddSingleton<IPlaybackSessionRunner, PlaybackSessionRunner>()
            .AddSingleton<IRadioSessionController, RadioSessionController>()
            .AddSingleton<ISettingsService, JsonSettingsService>()
            .AddSingleton<ISdrDevice>(sp =>
            {
                var settings = sp.GetRequiredService<ISettingsService>().LoadSettings();
                return SdrDeviceFactory.Create(settings.SdrDeviceType);
            });

    public static IServiceCollection AddSignalProcessingServices(
        this IServiceCollection services) =>
        services
            .AddSingleton<IAudioOutput, WaveOutAudioOutput>()
            .AddSingleton<IAudioFileReader, WavFilePlayer>()
            .AddSingleton<IInputSessionStateMachine, InputSessionStateMachine>()
            .AddSingleton<ISignalProcessingWorkerFactory, SignalProcessingWorkerFactory>()
            .AddSingleton<IPlaybackProcessor, PlaybackProcessor>()
            .AddSingleton<IAudioService, AudioService>()
            .AddSingleton<IGainUpdateWorkerFactory, GainUpdateWorkerFactory>()
            .AddSingleton<IAgcManagerFactory, AgcManagerFactory>()
            .AddSingleton<IFftProcessorFactory, FftProcessorFactory>()
            .AddSingleton<IMainFftWorkerFactory, MainFftWorkerFactory>()
            .AddSingleton<IMainFftServiceFactory, MainFftServiceFactory>()
            .AddSingleton<IGpuUsageMonitor, GpuUsageMonitor>()
            .AddSingleton<IRadioDiagnosticsStore, RadioDiagnosticsStore>()
            .AddSingleton<IRadioDiagnosticsCollector, RadioDiagnosticsCollector>()
            .AddSingleton<IRadioProcessingPipeline, RadioProcessingPipeline>();

    public static IServiceCollection AddPresentationServices(
        this IServiceCollection services) =>
        services
            .AddSingleton<IDialogService, WindowsDialogService>()
            .AddSingleton<ILastStateService, JsonLastStateService>()
            .AddSingleton<MainViewModel>()
            .AddTransient<MainWindow>();

    public static IServiceCollection AddApplicationStartupServices(
        this IServiceCollection services) =>
        services.AddSingleton<ApplicationStartupCoordinator>();
}
