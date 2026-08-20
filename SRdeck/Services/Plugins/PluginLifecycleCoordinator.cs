using SRdeckPlugin.Contracts;

namespace SRdeck.Services.Plugins;

internal sealed class PluginLifecycleCoordinator(
    PluginRuntimeRegistry runtimeRegistry,
    PluginManagerOptions options)
{
    private readonly PluginRuntimeRegistry _runtimeRegistry = runtimeRegistry;
    private readonly PluginManagerOptions _options = options;

    public async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        foreach (PluginRuntimeEntry entry in _runtimeRegistry.Entries)
        {
            if (!entry.IsCompatible || entry.State != PluginLifecycleState.Discovered) continue;
            await InitializeEntryAsync(entry, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask<PluginOperationResult> ActivateAsync(
        string pluginId,
        CancellationToken cancellationToken)
    {
        if (!_runtimeRegistry.TryGetEntry(pluginId, out PluginRuntimeEntry? target) || target is null)
            return PluginOperationResult.Failure($"Plugin '{pluginId}' is not registered.");
        if (!target.IsCompatible)
            return PluginOperationResult.Failure(target.LastError ?? $"Plugin '{pluginId}' is incompatible.");
        if (_runtimeRegistry.ActivePluginId == pluginId &&
            target.State is PluginLifecycleState.Active or PluginLifecycleState.Streaming)
            return PluginOperationResult.Success;

        if (target.State == PluginLifecycleState.Discovered)
        {
            PluginOperationResult initializeResult =
                await InitializeEntryAsync(target, cancellationToken).ConfigureAwait(false);
            if (!initializeResult.Succeeded) return initializeResult;
        }
        bool targetIsAlreadyActive = target.State is PluginLifecycleState.Active or PluginLifecycleState.Streaming;
        if (!targetIsAlreadyActive && target.State != PluginLifecycleState.Initialized)
            return PluginOperationResult.Failure($"Plugin '{pluginId}' cannot be activated from state {target.State}.");

        _runtimeRegistry.RemoveConcurrent(pluginId);
        PluginRuntimeEntry? previous = null;
        bool resumePreviousStream = false;
        if (_runtimeRegistry.ActivePluginId is not null &&
            _runtimeRegistry.TryGetEntry(_runtimeRegistry.ActivePluginId, out PluginRuntimeEntry? current) &&
            current is not null)
        {
            previous = current;
            resumePreviousStream = current.State == PluginLifecycleState.Streaming;
            PluginOperationResult deactivateResult =
                await DeactivateEntryAsync(current, cancellationToken).ConfigureAwait(false);
            if (!deactivateResult.Succeeded) return deactivateResult;
            _runtimeRegistry.SetActivePlugin(null);
        }

        if (targetIsAlreadyActive)
        {
            _runtimeRegistry.SetActivePlugin(pluginId);
            target.LastError = null;
            _runtimeRegistry.PublishChanged(target);
            return PluginOperationResult.Success;
        }

        // Activation may legitimately request tuning. Publish the candidate ID
        // before invoking the plugin, while capability lookup still remains
        // unavailable until the entry reaches Active state.
        _runtimeRegistry.SetActivePlugin(pluginId);
        _runtimeRegistry.AddActivating(pluginId);
        try
        {
            await InvokeLifecycleAsync(target.Module.ActivateAsync, "activate", cancellationToken).ConfigureAwait(false);
            target.State = PluginLifecycleState.Active;
            target.LastError = null;
            _runtimeRegistry.PublishChanged(target);
            return PluginOperationResult.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _runtimeRegistry.SetActivePlugin(null);
            throw;
        }
        catch (PluginActivationRejectedException exception)
        {
            _runtimeRegistry.SetActivePlugin(null);
            _runtimeRegistry.SetState(target, PluginLifecycleState.Initialized, exception.Message);
            return await RestorePreviousPluginAsync(previous, resumePreviousStream, exception.Message, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _runtimeRegistry.SetActivePlugin(null);
            PluginOperationResult failure = Fault(target, "activate", exception);
            return await RestorePreviousPluginAsync(previous, resumePreviousStream, failure.Error!, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _runtimeRegistry.RemoveActivating(pluginId);
        }
    }

    public async ValueTask<PluginOperationResult> ActivateAdditionalAsync(
        string pluginId,
        CancellationToken cancellationToken)
    {
        if (!_runtimeRegistry.TryGetEntry(pluginId, out PluginRuntimeEntry? target) || target is null)
            return PluginOperationResult.Failure($"Plugin '{pluginId}' is not registered.");
        if (!target.IsCompatible)
            return PluginOperationResult.Failure(target.LastError ?? $"Plugin '{pluginId}' is incompatible.");
        if (_runtimeRegistry.ActivePluginId == pluginId || _runtimeRegistry.IsConcurrent(pluginId))
            return PluginOperationResult.Success;
        if (target.State == PluginLifecycleState.Discovered)
        {
            PluginOperationResult initializeResult =
                await InitializeEntryAsync(target, cancellationToken).ConfigureAwait(false);
            if (!initializeResult.Succeeded) return initializeResult;
        }
        if (target.State != PluginLifecycleState.Initialized)
            return PluginOperationResult.Failure(
                $"Plugin '{pluginId}' cannot be activated concurrently from state {target.State}.");

        _runtimeRegistry.AddActivating(pluginId);
        try
        {
            await InvokeLifecycleAsync(target.Module.ActivateAsync, "activate", cancellationToken).ConfigureAwait(false);
            target.State = PluginLifecycleState.Active;
            target.LastError = null;
            _runtimeRegistry.AddConcurrent(pluginId);
            _runtimeRegistry.PublishChanged(target);
            return PluginOperationResult.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Fault(target, "activate", exception);
        }
        finally
        {
            _runtimeRegistry.RemoveActivating(pluginId);
        }
    }

    public async ValueTask<PluginOperationResult> SelectProfileAsync(
        string pluginId,
        string profileId,
        CancellationToken cancellationToken)
    {
        if (!_runtimeRegistry.TryGetEntry(pluginId, out PluginRuntimeEntry? entry) || entry is null)
            return PluginOperationResult.Failure($"Plugin '{pluginId}' is not registered.");
        if (!entry.IsCompatible)
            return PluginOperationResult.Failure(entry.LastError ?? $"Plugin '{pluginId}' is incompatible.");
        if (entry.Module is not IPluginProfileProvider provider)
            return PluginOperationResult.Failure($"Plugin '{pluginId}' does not expose profiles.");
        if (entry.State == PluginLifecycleState.Discovered)
        {
            PluginOperationResult initializeResult =
                await InitializeEntryAsync(entry, cancellationToken).ConfigureAwait(false);
            if (!initializeResult.Succeeded) return initializeResult;
        }
        if (entry.State == PluginLifecycleState.Streaming && provider is not ILivePluginProfileProvider)
            return PluginOperationResult.Failure($"Plugin '{pluginId}' cannot change profiles while streaming.");
        if (entry.State is not (PluginLifecycleState.Initialized or PluginLifecycleState.Active or PluginLifecycleState.Streaming))
            return PluginOperationResult.Failure($"Plugin '{pluginId}' cannot select a profile from state {entry.State}.");
        if (!provider.Profiles.Any(profile => string.Equals(profile.Id, profileId, StringComparison.Ordinal)))
            return PluginOperationResult.Failure($"Profile '{profileId}' is not registered by plugin '{pluginId}'.");

        try
        {
            await InvokeLifecycleAsync(
                token => provider.SelectProfileAsync(profileId, token),
                "select profile",
                cancellationToken).ConfigureAwait(false);
            entry.LastError = null;
            _runtimeRegistry.PublishChanged(entry);
            return PluginOperationResult.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Fault(entry, "select profile", exception);
        }
    }

    public async ValueTask<PluginOperationResult> StartStreamAsync(CancellationToken cancellationToken)
    {
        if (_runtimeRegistry.ActivePluginId is null ||
            !_runtimeRegistry.TryGetEntry(_runtimeRegistry.ActivePluginId, out PluginRuntimeEntry? entry) ||
            entry is null)
            return PluginOperationResult.Failure("No active plugin is selected.");
        if (entry.State == PluginLifecycleState.Streaming) return PluginOperationResult.Success;
        if (entry.State != PluginLifecycleState.Active)
            return PluginOperationResult.Failure(
                $"Plugin '{entry.Module.Descriptor.Id}' cannot start from state {entry.State}.");
        return await StartEntryAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<PluginOperationResult> StartStreamAsync(
        string pluginId,
        CancellationToken cancellationToken)
    {
        if (!_runtimeRegistry.IsPluginActive(pluginId) ||
            !_runtimeRegistry.TryGetEntry(pluginId, out PluginRuntimeEntry? entry) ||
            entry is null)
            return PluginOperationResult.Failure($"Plugin '{pluginId}' is not active.");
        return await StartEntryAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<PluginOperationResult> StopStreamAsync(CancellationToken cancellationToken)
    {
        if (_runtimeRegistry.ActivePluginId is null ||
            !_runtimeRegistry.TryGetEntry(_runtimeRegistry.ActivePluginId, out PluginRuntimeEntry? entry) ||
            entry is null)
            return PluginOperationResult.Success;
        return await StopEntryAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<PluginOperationResult> StopStreamAsync(
        string pluginId,
        CancellationToken cancellationToken)
    {
        if (!_runtimeRegistry.IsPluginActive(pluginId) ||
            !_runtimeRegistry.TryGetEntry(pluginId, out PluginRuntimeEntry? entry) ||
            entry is null)
            return PluginOperationResult.Success;
        return await StopEntryAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<PluginOperationResult> DeactivateAsync(
        string pluginId,
        CancellationToken cancellationToken)
    {
        if (!_runtimeRegistry.TryGetEntry(pluginId, out PluginRuntimeEntry? entry) ||
            entry is null ||
            !_runtimeRegistry.IsPluginActive(pluginId))
            return PluginOperationResult.Success;
        PluginOperationResult result = await DeactivateEntryAsync(entry, cancellationToken).ConfigureAwait(false);
        if (result.Succeeded)
        {
            _runtimeRegistry.RemoveConcurrent(pluginId);
            if (_runtimeRegistry.ActivePluginId == pluginId) _runtimeRegistry.SetActivePlugin(null);
        }
        return result;
    }

    public async ValueTask ShutdownAsync(CancellationToken cancellationToken)
    {
        foreach (string pluginId in _runtimeRegistry.ActivePluginIds.Reverse())
        {
            if (_runtimeRegistry.TryGetEntry(pluginId, out PluginRuntimeEntry? active) && active is not null)
                await DeactivateEntryAsync(active, cancellationToken).ConfigureAwait(false);
        }
        _runtimeRegistry.ClearActiveMembership();

        foreach (PluginRuntimeEntry entry in _runtimeRegistry.Entries.Reverse())
        {
            if (entry.State == PluginLifecycleState.Disposed) continue;
            try
            {
                await InvokeDisposeAsync(entry.Module, cancellationToken).ConfigureAwait(false);
                _runtimeRegistry.SetState(entry, PluginLifecycleState.Disposed, entry.LastError);
            }
            catch (Exception exception)
            {
                Fault(entry, "dispose", exception);
            }
        }
    }

    public void ReportFault(string pluginId, string operation, Exception exception)
    {
        if (_runtimeRegistry.TryGetEntry(pluginId, out PluginRuntimeEntry? entry) &&
            entry is not null &&
            entry.State != PluginLifecycleState.Disposed)
            Fault(entry, operation, exception);
    }

    private async ValueTask<PluginOperationResult> InitializeEntryAsync(
        PluginRuntimeEntry entry,
        CancellationToken cancellationToken)
    {
        try
        {
            await InvokeLifecycleAsync(
                token => entry.Module.InitializeAsync(entry.Context, token),
                "initialize",
                cancellationToken).ConfigureAwait(false);
            _runtimeRegistry.SetState(entry, PluginLifecycleState.Initialized);
            return PluginOperationResult.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Fault(entry, "initialize", exception);
        }
    }

    private async ValueTask<PluginOperationResult> StopEntryAsync(
        PluginRuntimeEntry entry,
        CancellationToken cancellationToken)
    {
        if (entry.State != PluginLifecycleState.Streaming) return PluginOperationResult.Success;
        try
        {
            await InvokeLifecycleAsync(entry.Module.StopStreamAsync, "stop stream", cancellationToken).ConfigureAwait(false);
            _runtimeRegistry.SetState(entry, PluginLifecycleState.Active);
            return PluginOperationResult.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Fault(entry, "stop stream", exception);
        }
    }

    private async ValueTask<PluginOperationResult> StartEntryAsync(
        PluginRuntimeEntry entry,
        CancellationToken cancellationToken)
    {
        if (entry.State == PluginLifecycleState.Streaming) return PluginOperationResult.Success;
        if (entry.State != PluginLifecycleState.Active)
            return PluginOperationResult.Failure(
                $"Plugin '{entry.Module.Descriptor.Id}' cannot start from state {entry.State}.");
        try
        {
            await InvokeLifecycleAsync(entry.Module.StartStreamAsync, "start stream", cancellationToken)
                .ConfigureAwait(false);
            _runtimeRegistry.SetState(entry, PluginLifecycleState.Streaming);
            return PluginOperationResult.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PluginActivationRejectedException exception)
        {
            return PluginOperationResult.Failure(exception.Message);
        }
        catch (Exception exception)
        {
            return Fault(entry, "start stream", exception);
        }
    }

    private async ValueTask<PluginOperationResult> DeactivateEntryAsync(
        PluginRuntimeEntry entry,
        CancellationToken cancellationToken)
    {
        PluginOperationResult stopResult = await StopEntryAsync(entry, cancellationToken).ConfigureAwait(false);
        if (!stopResult.Succeeded) return stopResult;
        if (entry.State == PluginLifecycleState.Initialized) return PluginOperationResult.Success;
        if (entry.State != PluginLifecycleState.Active)
            return PluginOperationResult.Failure(
                $"Plugin '{entry.Module.Descriptor.Id}' cannot be deactivated from state {entry.State}.");

        try
        {
            await InvokeLifecycleAsync(entry.Module.DeactivateAsync, "deactivate", cancellationToken).ConfigureAwait(false);
            _runtimeRegistry.SetState(entry, PluginLifecycleState.Initialized);
            return PluginOperationResult.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Fault(entry, "deactivate", exception);
        }
    }

    private PluginOperationResult Fault(PluginRuntimeEntry entry, string operation, Exception exception) =>
        _runtimeRegistry.TransitionToFaulted(entry, operation, exception);

    private async ValueTask<PluginOperationResult> RestorePreviousPluginAsync(
        PluginRuntimeEntry? previous,
        bool resumeStream,
        string activationError,
        CancellationToken cancellationToken)
    {
        if (previous is null) return PluginOperationResult.Failure(activationError);

        _runtimeRegistry.SetActivePlugin(previous.Module.Descriptor.Id);
        _runtimeRegistry.AddActivating(previous.Module.Descriptor.Id);
        try
        {
            await InvokeLifecycleAsync(previous.Module.ActivateAsync, "restore", cancellationToken)
                .ConfigureAwait(false);
            _runtimeRegistry.SetState(previous, PluginLifecycleState.Active);
            if (resumeStream)
            {
                await InvokeLifecycleAsync(previous.Module.StartStreamAsync, "restore stream", cancellationToken)
                    .ConfigureAwait(false);
                _runtimeRegistry.SetState(previous, PluginLifecycleState.Streaming);
            }
            return PluginOperationResult.Failure(activationError);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _runtimeRegistry.SetActivePlugin(null);
            throw;
        }
        catch (Exception exception)
        {
            _runtimeRegistry.SetActivePlugin(null);
            PluginOperationResult rollbackFailure = Fault(previous, "restore", exception);
            return PluginOperationResult.Failure(
                $"{activationError} The previous plugin could not be restored: {rollbackFailure.Error}");
        }
        finally
        {
            _runtimeRegistry.RemoveActivating(previous.Module.Descriptor.Id);
        }
    }

    private async Task InvokeLifecycleAsync(
        Func<CancellationToken, ValueTask> operation,
        string operationName,
        CancellationToken cancellationToken)
    {
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task operationTask = operation(operationCancellation.Token).AsTask();
        try
        {
            await operationTask
                .WaitAsync(_options.LifecycleOperationTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            operationCancellation.Cancel();
            throw new TimeoutException(
                $"Plugin lifecycle operation '{operationName}' exceeded {_options.LifecycleOperationTimeout}.",
                exception);
        }
    }

    private async Task InvokeDisposeAsync(IPluginModule module, CancellationToken cancellationToken)
    {
        try
        {
            await module.DisposeAsync().AsTask()
                .WaitAsync(_options.LifecycleOperationTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException(
                $"Plugin lifecycle operation 'dispose' exceeded {_options.LifecycleOperationTimeout}.",
                exception);
        }
    }
}
