using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using SRdeckPlugin.Contracts;

namespace SRdeck.Services.Plugins;

internal sealed class PluginRuntimeRegistry
{
    private static readonly Regex ValidId = new(
        "^[a-z0-9]+(?:[.-][a-z0-9]+)*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly Dictionary<string, PluginRuntimeEntry> _entries;
    private readonly ConcurrentDictionary<string, byte> _concurrentPluginIds =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _activatingPluginIds =
        new(StringComparer.Ordinal);
    private string? _activePluginId;

    public PluginRuntimeRegistry(
        IEnumerable<IPluginModule> modules,
        IPluginHostContextFactory contextFactory)
    {
        _entries = new Dictionary<string, PluginRuntimeEntry>(StringComparer.Ordinal);

        foreach (IPluginModule module in modules)
        {
            ValidateDescriptor(module.Descriptor);
            if (!_entries.TryAdd(module.Descriptor.Id, CreateEntry(module, contextFactory)))
                throw new InvalidOperationException($"Duplicate plugin ID '{module.Descriptor.Id}'.");
        }
    }

    public event EventHandler<PluginRuntimeChangedEventArgs>? RuntimeChanged;

    public IReadOnlyList<PluginRuntimeInfo> Plugins => _entries.Values
        .Select(entry => entry.ToRuntimeInfo())
        .OrderBy(info => info.Descriptor.DisplayName, StringComparer.CurrentCulture)
        .ToArray();

    public string? ActivePluginId => _activePluginId;

    public IReadOnlyList<string> ActivePluginIds => ActiveIdsSnapshot(streamingOnly: false);

    public IReadOnlyList<string> StreamingPluginIds => ActiveIdsSnapshot(streamingOnly: true);

    public bool IsActivePluginStreaming =>
        _activePluginId is not null &&
        _entries.TryGetValue(_activePluginId, out PluginRuntimeEntry? entry) &&
        entry.State == PluginLifecycleState.Streaming;

    public bool IsPluginActive(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        return _activePluginId == pluginId || _concurrentPluginIds.ContainsKey(pluginId) ||
            _activatingPluginIds.ContainsKey(pluginId);
    }

    public bool IsPluginStreaming(string pluginId) => IsPluginActive(pluginId) &&
        _entries.TryGetValue(pluginId, out PluginRuntimeEntry? entry) &&
        entry.State == PluginLifecycleState.Streaming;

    public bool TryGetEntry(string pluginId, out PluginRuntimeEntry? entry) =>
        _entries.TryGetValue(pluginId, out entry);

    public IEnumerable<PluginRuntimeEntry> Entries => _entries.Values;

    public bool TryGetActiveCapability<TCapability>(out TCapability? capability) where TCapability : class
    {
        string? activePluginId = _activePluginId;
        if (activePluginId is not null &&
            _entries.TryGetValue(activePluginId, out PluginRuntimeEntry? entry) &&
            entry.State is PluginLifecycleState.Active or PluginLifecycleState.Streaming &&
            entry.Module is TCapability value)
        {
            capability = value;
            return true;
        }

        capability = null;
        return false;
    }

    public void SetState(PluginRuntimeEntry entry, PluginLifecycleState state, string? lastError = null)
    {
        entry.State = state;
        entry.LastError = lastError;
        NotifyChanged(entry);
    }

    public void SetActivePlugin(string? pluginId) => _activePluginId = pluginId;

    public void AddActivating(string pluginId) => _activatingPluginIds.TryAdd(pluginId, 0);

    public void RemoveActivating(string pluginId) => _activatingPluginIds.TryRemove(pluginId, out _);

    public void AddConcurrent(string pluginId) => _concurrentPluginIds.TryAdd(pluginId, 0);

    public void RemoveConcurrent(string pluginId) => _concurrentPluginIds.TryRemove(pluginId, out _);

    public bool IsConcurrent(string pluginId) => _concurrentPluginIds.ContainsKey(pluginId);

    public void ClearActiveMembership()
    {
        _activePluginId = null;
        _concurrentPluginIds.Clear();
        _activatingPluginIds.Clear();
    }

    public void PublishChanged(PluginRuntimeEntry entry) => NotifyChanged(entry);

    public PluginOperationResult TransitionToFaulted(
        PluginRuntimeEntry entry,
        string operation,
        Exception exception)
    {
        string message = $"Failed to {operation} plugin '{entry.Module.Descriptor.Id}': {exception.Message}";
        entry.LastError = message;
        entry.Context.Logger.Log(PluginLogLevel.Error, $"plugin.{operation.Replace(' ', '.')}.failed", message, exception);
        if (_activePluginId == entry.Module.Descriptor.Id) _activePluginId = null;
        _concurrentPluginIds.TryRemove(entry.Module.Descriptor.Id, out _);
        _activatingPluginIds.TryRemove(entry.Module.Descriptor.Id, out _);
        // Publish Faulted only after active membership has been removed. Readers
        // that observe Faulted must never still see the plugin as active.
        entry.State = PluginLifecycleState.Faulted;
        NotifyChanged(entry);
        return PluginOperationResult.Failure(message);
    }

    private IReadOnlyList<string> ActiveIdsSnapshot(bool streamingOnly)
    {
        string? primary = _activePluginId;
        IEnumerable<string> ids = primary is null
            ? _concurrentPluginIds.Keys
            : new[] { primary }.Concat(_concurrentPluginIds.Keys.Where(id => id != primary));
        return ids.Distinct(StringComparer.Ordinal)
            .Where(id => _entries.TryGetValue(id, out PluginRuntimeEntry? entry) &&
                (streamingOnly
                    ? entry.State == PluginLifecycleState.Streaming
                    : entry.State is PluginLifecycleState.Active or PluginLifecycleState.Streaming))
            .ToArray();
    }

    private void NotifyChanged(PluginRuntimeEntry entry) =>
        RuntimeChanged?.Invoke(this, new PluginRuntimeChangedEventArgs(entry.ToRuntimeInfo()));

    private static PluginRuntimeEntry CreateEntry(
        IPluginModule module,
        IPluginHostContextFactory contextFactory)
    {
        ValidateProfiles(module);
        bool compatible = module.Descriptor.MinimumHostApiVersion <= PluginHostApi.CurrentVersion &&
                          module.Descriptor.MaximumHostApiVersion >= PluginHostApi.CurrentVersion;
        string? error = compatible
            ? null
            : $"Plugin '{module.Descriptor.Id}' supports host API " +
              $"{module.Descriptor.MinimumHostApiVersion} through {module.Descriptor.MaximumHostApiVersion}; " +
              $"the current API is {PluginHostApi.CurrentVersion}.";
        return new PluginRuntimeEntry(module, contextFactory.Create(module.Descriptor.Id), compatible, error);
    }

    private static void ValidateProfiles(IPluginModule module)
    {
        if (module is not IPluginProfileProvider provider) return;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        int defaultCount = 0;
        foreach (PluginProfileDescriptor profile in provider.Profiles)
        {
            if (string.IsNullOrWhiteSpace(profile.Id) || !ValidId.IsMatch(profile.Id))
                throw new InvalidOperationException($"Plugin '{module.Descriptor.Id}' has invalid profile ID '{profile.Id}'.");
            if (string.IsNullOrWhiteSpace(profile.DisplayName))
                throw new InvalidOperationException($"Profile '{profile.Id}' has no display name.");
            if (!ids.Add(profile.Id))
                throw new InvalidOperationException($"Plugin '{module.Descriptor.Id}' has duplicate profile ID '{profile.Id}'.");
            if (profile.IsDefault) defaultCount++;
        }
        if (defaultCount > 1)
            throw new InvalidOperationException($"Plugin '{module.Descriptor.Id}' has multiple default profiles.");
    }

    private static void ValidateDescriptor(PluginDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (string.IsNullOrWhiteSpace(descriptor.Id) || !ValidId.IsMatch(descriptor.Id))
            throw new InvalidOperationException($"Invalid plugin ID '{descriptor.Id}'.");
        if (string.IsNullOrWhiteSpace(descriptor.DisplayName))
            throw new InvalidOperationException($"Plugin '{descriptor.Id}' has no display name.");
        if (descriptor.MinimumHostApiVersion > descriptor.MaximumHostApiVersion)
            throw new InvalidOperationException($"Plugin '{descriptor.Id}' has an invalid host API range.");
    }
}

internal sealed class PluginRuntimeEntry(
    IPluginModule module,
    IPluginHostContext context,
    bool isCompatible,
    string? lastError)
{
    public IPluginModule Module { get; } = module;
    public IPluginHostContext Context { get; } = context;
    public bool IsCompatible { get; } = isCompatible;
    public PluginLifecycleState State { get; set; } = PluginLifecycleState.Discovered;
    public string? LastError { get; set; } = lastError;

    public PluginRuntimeInfo ToRuntimeInfo() => new(
        Module.Descriptor,
        State,
        IsCompatible,
        LastError,
        (Module as IPluginProfileProvider)?.SelectedProfileId);
}
