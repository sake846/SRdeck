using SRdeckPlugin.Contracts;
using SRdeckPlugin.Sdk;

namespace SRdeckPlugin.Example;

public sealed class ExamplePlugin : PluginModuleBase, IIqBlockConsumer
{
    private readonly IqStreamContinuityTracker continuity = new();
    private long processedSamples;

    public ExamplePlugin() => RegisterStreamReset(continuity.Reset);

    public override PluginDescriptor Descriptor { get; } = new(
        Id: "example.decoder",
        DisplayName: "Example decoder",
        Description: "Minimal headless raw-IQ plugin",
        PluginVersion: new Version(1, 0),
        MinimumHostApiVersion: new Version(1, 0),
        MaximumHostApiVersion: new Version(1, 0),
        Capabilities: PluginCapabilities.IqConsumer | PluginCapabilities.Headless,
        Provider: "Example provider",
        License: "License name");

    public PluginIqPreferences IqPreferences { get; } = new(4);
    public long ProcessedSamples => Interlocked.Read(ref processedSamples);

    protected override ValueTask OnStartStreamAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Exchange(ref processedSamples, 0);
        return ValueTask.CompletedTask;
    }

    public ValueTask ConsumeAsync(IIqBlockLease block, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (State != PluginLifecycleState.Streaming)
            return ValueTask.CompletedTask;

        IqStreamTransition transition = continuity.Observe(block.Metadata);
        if (transition.RequiresReset)
        {
            // Reset protocol-specific filters, synchronizers, and delayed work here.
        }

        // Samples are valid only until this callback returns. Copy them during
        // the callback if protocol-specific work must continue asynchronously.
        Interlocked.Add(ref processedSamples, block.Samples.Length);
        return ValueTask.CompletedTask;
    }
}
