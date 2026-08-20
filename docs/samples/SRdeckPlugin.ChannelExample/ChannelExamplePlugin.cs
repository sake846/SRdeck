using SRdeckPlugin.Contracts;
using SRdeckPlugin.Sdk;

namespace SRdeckPlugin.ChannelExample;

public sealed class ChannelExamplePlugin :
    PluginModuleBase,
    IPluginProfileProvider,
    IPluginChannelBlockConsumer,
    IPluginProcessingDiagnosticsProvider
{
    private const long TargetFrequencyHz = 100_000_000;
    private static readonly IReadOnlyList<PluginProfileDescriptor> profiles =
    [
        new("default", "Default", "Example 12 kHz channel", IsDefault: true)
    ];
    private static readonly IReadOnlyList<PluginChannelRequest> channelRequests =
    [
        new(
            Id: "primary",
            CenterFrequencyHz: TargetFrequencyHz,
            BandwidthHz: 12_000,
            OutputSampleRateHz: 48_000,
            AllowRawIqFallback: false)
    ];
    private readonly IqStreamContinuityTracker continuity = new();

    public ChannelExamplePlugin() => RegisterStreamReset(continuity.Reset);

    public override PluginDescriptor Descriptor { get; } = new(
        Id: "example.channel-decoder",
        DisplayName: "Example channel decoder",
        Description: "Minimal standard-channel IQ plugin",
        PluginVersion: new Version(1, 0),
        MinimumHostApiVersion: new Version(1, 0),
        MaximumHostApiVersion: new Version(1, 0),
        Capabilities: PluginCapabilities.ChannelIqConsumer | PluginCapabilities.Headless,
        Provider: "Example provider",
        License: "License name");

    public IReadOnlyList<PluginProfileDescriptor> Profiles => profiles;
    public string? SelectedProfileId => "default";
    public IReadOnlyList<PluginChannelRequest> ChannelRequests => channelRequests;

    public PluginProcessingStageDefinition ProcessingStage { get; } = new(
        Operation: "Example detection and decode",
        Device: PluginComputeDevice.Cpu,
        Backend: ".NET CPU");

    public ValueTask SelectProfileAsync(string profileId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (!string.Equals(profileId, "default", StringComparison.Ordinal))
            throw new ArgumentException($"Unknown profile '{profileId}'.", nameof(profileId));
        return ValueTask.CompletedTask;
    }

    protected override async ValueTask OnActivateAsync(CancellationToken cancellationToken)
    {
        IPluginHostContext host = HostContext
            ?? throw new InvalidOperationException("The plugin has not been initialized.");
        PluginTuningResult result = await host.Tuning.RequestAsync(
            new PluginTuningRequest(
                ProfileId: "default",
                DisplayName: "Example channel",
                Targets: [new TuningTarget(TargetFrequencyHz, 12_000)],
                PreferredCenterFrequencyHz: TargetFrequencyHz,
                MinimumSampleRateHz: 48_000,
                FrequencyStepHz: null,
                RequiresContinuousReception: true,
                AllowsScanning: false,
                GainPreference: PluginGainPreference.Unspecified),
            cancellationToken).ConfigureAwait(false);

        if (result.Outcome is PluginTuningOutcome.Rejected or PluginTuningOutcome.Deferred)
            throw new PluginActivationRejectedException(result.Message);
        if (TargetFrequencyHz < result.PassbandLowerFrequencyHz ||
            TargetFrequencyHz > result.PassbandUpperFrequencyHz)
            throw new PluginActivationRejectedException(
                "The applied passband does not contain the requested channel.");
    }

    public ValueTask ConsumeChannelsAsync(
        IReadOnlyList<IChannelIqBlockLease> blocks,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (State != PluginLifecycleState.Streaming)
            return ValueTask.CompletedTask;

        foreach (IChannelIqBlockLease block in blocks)
        {
            if (!string.Equals(block.Metadata.Configuration.RequestId, "primary", StringComparison.Ordinal))
                continue;

            IqStreamTransition transition = continuity.Observe(block.Metadata.Source);
            if (transition.RequiresReset)
            {
                // Reset protocol-specific state before consuming this block.
            }

            long sourcePosition = block.Metadata.MapOutputToSource(0);
            HostContext?.Metrics.AddCounter(
                PluginProcessingStage.Input,
                "channel.samples",
                block.Samples.Length,
                "samples");
            _ = sourcePosition;
        }

        return ValueTask.CompletedTask;
    }
}
