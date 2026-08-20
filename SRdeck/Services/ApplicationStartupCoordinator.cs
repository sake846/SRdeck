using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using SRdeckPlugin.Contracts;
using SRdeck.Configuration;
using SRdeck.Models;
using SRdeck.Services.Plugins;
using SRdeck.ViewModels;

namespace SRdeck.Services;

internal sealed class ApplicationStartupCoordinator
{
    private readonly IServiceProvider services;
    private readonly IReadOnlyList<IPluginModule> pluginModules;

    public ApplicationStartupCoordinator(
        IServiceProvider services,
        IReadOnlyList<IPluginModule> pluginModules)
    {
        this.services = services;
        this.pluginModules = pluginModules;
    }

    public static IReadOnlyList<IPluginModule> DiscoverPluginModules() =>
        PluginModuleCatalog.Discover();

    public async Task<ApplicationStartupResult> StartAsync(
        bool isHeadless,
        IApplicationStartupProgress progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(progress);

        if (!isHeadless)
            progress.Report("プラグイン受信処理を事前準備中…");

        try
        {
            PluginCodeWarmupResult codeWarmup = await services
                .GetRequiredService<PluginCodeWarmupService>()
                .WarmUpAsync(
                    pluginModules,
                    [
                        typeof(CoreEngine).Assembly,
                        typeof(SRdeck.DSP.IqSampleRingBuffer).Assembly
                    ],
                    cancellationToken);
            Console.WriteLine(
                $"Plugin code warm-up: {codeWarmup.PreparedMethodCount} methods in " +
                $"{codeWarmup.Elapsed.TotalMilliseconds:F0} ms " +
                $"({codeWarmup.FailedMethodCount} skipped by runtime)");
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[Startup] Plugin code warm-up skipped: {exception.Message}");
        }

        string? finalCalibrationStatus = null;
        long calibrationStatusShown = 0;
        if (!isHeadless)
            progress.Report("Channel Auto: CPU/GPU性能を測定中…");

        GpuChannelCalibrationResult calibration = await services
            .GetRequiredService<GpuChannelCalibrationService>()
            .CalibrateIfNeededAsync(cancellationToken);
        finalCalibrationStatus = FormatCalibrationStatus(calibration);
        if (!isHeadless)
            progress.Report(finalCalibrationStatus);
        calibrationStatusShown = !isHeadless ? Stopwatch.GetTimestamp() : 0;
        if (calibration.Profile is not null)
        {
            string selected = string.Join(", ", calibration.Profile.Entries.Select(entry =>
                $"{entry.Workload}={(entry.UseGpu ? "GPU" : "CPU")}"));
            Console.WriteLine(
                $"GPU channel calibration ({calibration.Source}): {selected}");
        }

        var engine = services.GetRequiredService<ISdrEngine>();
        var sdrDevice = services.GetRequiredService<ISdrDevice>();
        var mainViewModel = services.GetRequiredService<MainViewModel>();
        engine.SdrDevice = sdrDevice;
        mainViewModel.Initialize();

        var pluginManager = services.GetRequiredService<IPluginManager>();
        await pluginManager.InitializeAsync();
        string? requestedPluginId = engine.InitialAppSettings.Plugins.SelectedPluginId;
        PluginRuntimeInfo? selectedPlugin = PluginSelectionPolicy.Select(
            pluginManager.Plugins,
            requestedPluginId,
            !isHeadless);
        if (selectedPlugin is not null)
        {
            PluginOperationResult activation = await pluginManager.ActivateAsync(
                selectedPlugin.Descriptor.Id);
            if (!activation.Succeeded) Console.Error.WriteLine(activation.Error);
        }

        if (!isHeadless)
            progress.Report("FFT・プラグインDSPを準備中…");
        long processingWarmupStarted = Stopwatch.GetTimestamp();
        await Task.Run(async () =>
        {
            try
            {
                engine.WarmUpForSdrStart();
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"[Startup] FFT warm-up skipped: {exception.Message}");
            }

            RadioControl startupControl = engine.Control;
            var processingWarmupContext = new PluginProcessingWarmupContext(
                startupControl.FsHz,
                startupControl.CenterFreqHz);
            foreach (IPluginProcessingWarmup warmup in
                     pluginModules.OfType<IPluginProcessingWarmup>())
            {
                long pluginWarmupStarted = Stopwatch.GetTimestamp();
                try
                {
                    await warmup.WarmUpProcessingAsync(
                        processingWarmupContext,
                        CancellationToken.None);
                    Console.WriteLine(
                        $"{((IPluginModule)warmup).Descriptor.DisplayName} DSP warm-up: " +
                        $"{Stopwatch.GetElapsedTime(pluginWarmupStarted).TotalMilliseconds:F0} ms");
                }
                catch (Exception exception)
                {
                    Debug.WriteLine(
                        $"[Startup] {((IPluginModule)warmup).Descriptor.Id} DSP warm-up skipped: " +
                        exception.Message);
                }
            }

            services.GetRequiredService<IPluginIqDispatcher>().WarmUpActiveChannels(
                startupControl.FsHz,
                startupControl.CenterFreqHz);
        });
        Console.WriteLine(
            $"Startup processing warm-up: " +
            $"{Stopwatch.GetElapsedTime(processingWarmupStarted).TotalMilliseconds:F0} ms");

        if (!isHeadless)
        {
            progress.Report(finalCalibrationStatus ?? "起動準備が完了しました");
            calibrationStatusShown = Stopwatch.GetTimestamp();
        }
        mainViewModel.SyncPluginSelectionFromManager();

        return new ApplicationStartupResult(calibrationStatusShown);
    }

    private static string FormatCalibrationStatus(GpuChannelCalibrationResult result)
    {
        if (result.Profile is null)
        {
            return result.Source == GpuChannelCalibrationSource.Unavailable
                ? "Channel Auto: CPU（対応GPUなし）"
                : "Channel Auto: CPU（性能測定を完了できませんでした）";
        }

        string selections = string.Join(" ｜ ", result.Profile.Entries
            .OrderBy(entry => entry.Workload)
            .Select(entry =>
                $"{FormatWorkload(entry.Workload)} {(entry.UseGpu ? "GPU" : "CPU")}"));
        return $"Channel Auto: {selections}";
    }

    private static string FormatWorkload(GpuChannelWorkloadClass workload) => workload switch
    {
        GpuChannelWorkloadClass.Light => "軽量",
        GpuChannelWorkloadClass.Standard => "標準",
        GpuChannelWorkloadClass.Heavy => "重量",
        _ => workload.ToString()
    };
}

internal sealed record ApplicationStartupResult(long CalibrationStatusShownTimestamp);
