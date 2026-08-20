using SRdeckPlugin.Contracts;

namespace SRdeck.Services.Plugins;

public static class PluginSelectionPolicy
{
    public static PluginRuntimeInfo? Select(
        IReadOnlyList<PluginRuntimeInfo> plugins,
        string? requestedPluginId,
        bool isUiAvailable)
    {
        IEnumerable<PluginRuntimeInfo> eligible = plugins.Where(plugin =>
            plugin.IsCompatible &&
            (isUiAvailable || plugin.Descriptor.Capabilities.HasFlag(PluginCapabilities.Headless)));

        PluginRuntimeInfo? requested = eligible.FirstOrDefault(plugin =>
            string.Equals(plugin.Descriptor.Id, requestedPluginId, StringComparison.Ordinal));
        if (requested is not null) return requested;

        return isUiAvailable
            ? eligible.FirstOrDefault(plugin =>
                  plugin.Descriptor.IsEnabledByDefault &&
                  plugin.Descriptor.Capabilities.HasFlag(PluginCapabilities.MainView))
              ?? eligible.FirstOrDefault(plugin =>
                  plugin.Descriptor.Capabilities.HasFlag(PluginCapabilities.MainView))
              ?? eligible.FirstOrDefault()
            : eligible.FirstOrDefault(plugin =>
                  plugin.Descriptor.IsEnabledByDefault &&
                  plugin.Descriptor.Capabilities.HasFlag(PluginCapabilities.Headless))
              ?? eligible.FirstOrDefault(plugin =>
                plugin.Descriptor.Capabilities.HasFlag(PluginCapabilities.Headless));
    }
}
