using SRdeckPlugin.Contracts;

namespace SRdeck.Services.Plugins;

/// <summary>
/// Compatibility facade for the metrics registry moved to the plugin SDK.
/// </summary>
public interface IPluginMetricsRegistry
{
    IPluginMetrics GetOrCreate(string pluginId);
    PluginMetricsSnapshot GetSnapshot(string pluginId);
}

/// <summary>
/// Compatibility facade for <see cref="SRdeckPlugin.Sdk.PluginMetricsRegistry"/>.
/// </summary>
public sealed class PluginMetricsRegistry(TimeProvider timeProvider) : IPluginMetricsRegistry
{
    private readonly SRdeckPlugin.Sdk.PluginMetricsRegistry _inner = new(timeProvider);

    public IPluginMetrics GetOrCreate(string pluginId) => _inner.GetOrCreate(pluginId);

    public PluginMetricsSnapshot GetSnapshot(string pluginId) => _inner.GetSnapshot(pluginId);
}
