using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SRdeckPlugin.Contracts;
using SRdeck.Models;

namespace SRdeck.Services.Plugins;

public interface IPluginTuningServiceFactory
{
    IPluginTuningService Create(string pluginId);
}

public sealed class PluginTuningServiceFactory(
    Func<IPluginManager> pluginManager,
    IRadioControlStore controlStore,
    IRadioControlUpdatePublisher updatePublisher) : IPluginTuningServiceFactory
{
    public IPluginTuningService Create(string pluginId) => new PluginTuningService(
        pluginId,
        pluginManager,
        controlStore,
        updatePublisher);
}

internal sealed class PluginTuningService : IPluginTuningService
{
    private const double UsableNyquistRatio = 0.475;
    private readonly string _pluginId;
    private readonly Func<IPluginManager> _pluginManager;
    private readonly IRadioControlStore _controlStore;
    private readonly IRadioControlUpdatePublisher _updatePublisher;
    private int _isApplyingRequest;
    private int _hasAppliedRequest;
    private PluginTuningResult _current = new(
        PluginTuningOutcome.Deferred,
        "No tuning request has been applied.",
        0,
        0,
        0,
        0);

    public PluginTuningService(
        string pluginId,
        Func<IPluginManager> pluginManager,
        IRadioControlStore controlStore,
        IRadioControlUpdatePublisher updatePublisher)
    {
        _pluginId = pluginId;
        _pluginManager = pluginManager;
        _controlStore = controlStore;
        _updatePublisher = updatePublisher;
        _controlStore.Changed += OnRadioControlChanged;
    }

    public PluginTuningResult Current => Volatile.Read(ref _current);
    public event EventHandler<PluginTuningResult>? AppliedConfigurationChanged;

    public ValueTask<PluginTuningResult> RequestAsync(
        PluginTuningRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        IPluginManager manager = _pluginManager();
        if (!manager.IsPluginActive(_pluginId))
            return ValueTask.FromResult(SetCurrent(Rejected("Only an active plugin may request tuning."), false));
        if (!TryValidate(request, out string error))
            return ValueTask.FromResult(SetCurrent(Rejected(error), false));

        RadioControl before = _controlStore.Snapshot;
        int sampleRateHz = before.FsHz;
        if (sampleRateHz <= 0)
            return ValueTask.FromResult(SetCurrent(Rejected("The host sample rate is not configured."), false));
        if (sampleRateHz < request.MinimumSampleRateHz)
        {
            return ValueTask.FromResult(SetCurrent(Rejected(
                $"The host sample rate of {sampleRateHz} Hz is below the required minimum of {request.MinimumSampleRateHz} Hz.",
                sampleRateHz), false));
        }
        long lowerEdgeHz = request.Targets.Min(target => target.FrequencyHz - target.BandwidthHz / 2L);
        long upperEdgeHz = request.Targets.Max(target => target.FrequencyHz + target.BandwidthHz / 2L);
        long requiredWidthHz = upperEdgeHz - lowerEdgeHz;
        if (requiredWidthHz > sampleRateHz * UsableNyquistRatio * 2.0)
        {
            return ValueTask.FromResult(SetCurrent(Rejected(
                $"The requested {requiredWidthHz} Hz span does not fit in the {sampleRateHz} Hz sample rate.",
                sampleRateHz), false));
        }

        if (manager.ActivePluginId is string primaryPluginId && primaryPluginId != _pluginId)
        {
            long sharedHalfWidth = (long)(sampleRateHz * UsableNyquistRatio);
            long sharedLowerHz = before.CenterFreqHz - sharedHalfWidth;
            long sharedUpperHz = before.CenterFreqHz + sharedHalfWidth;
            if (lowerEdgeHz < sharedLowerHz || upperEdgeHz > sharedUpperHz)
                return ValueTask.FromResult(SetCurrent(Rejected(
                    "The additional plugin targets do not fit in the active shared passband."), false));
            var sharedResult = new PluginTuningResult(
                request.PreferredCenterFrequencyHz == before.CenterFreqHz
                    ? PluginTuningOutcome.Accepted
                    : PluginTuningOutcome.Adjusted,
                "The additional plugin is using the current shared tuning configuration.",
                before.CenterFreqHz,
                sampleRateHz,
                sharedLowerHz,
                sharedUpperHz,
                TargetFrequencyHz: before.TunedFreqHz);
            Volatile.Write(ref _hasAppliedRequest, 1);
            return ValueTask.FromResult(SetCurrent(sharedResult, true));
        }

        long requestedCenterFrequencyHz = ResolveCenterFrequency(request, lowerEdgeHz, upperEdgeHz, sampleRateHz);
        if (requestedCenterFrequencyHz is <= 0 or > int.MaxValue)
            return ValueTask.FromResult(SetCurrent(Rejected("The requested center frequency is outside the host range."), false));

        RadioControl requestedControl = before;
        requestedControl.CenterFreqHz = (int)requestedCenterFrequencyHz;
        requestedControl.TunedFreqHz = (int)Math.Clamp(request.Targets[0].FrequencyHz, 0, int.MaxValue);
        requestedControl.FreqOffsetHz = requestedControl.TunedFreqHz - requestedControl.CenterFreqHz;
        requestedControl.SpanHz = request.Targets.Max(target => target.BandwidthHz);
        if (request.FrequencyStepHz is > 0) requestedControl.StepHz = request.FrequencyStepHz.Value;

        bool resetMainViewZoom = requestedCenterFrequencyHz != before.CenterFreqHz &&
            requestedControl.BaseMainSpanHz > 0 &&
            requestedControl.MainSpanHz > 0 &&
            requestedControl.MainSpanHz < requestedControl.BaseMainSpanHz;
        if (resetMainViewZoom)
        {
            requestedControl.MainSpanHz = requestedControl.BaseMainSpanHz;
        }

        long centerFrequencyHz = request.PreservePreferredCenterFrequency
            ? requestedCenterFrequencyHz
            : TuningCoordinator.RoundInputCenterFrequency(requestedControl);
        requestedControl.CenterFreqHz = (int)centerFrequencyHz;
        requestedControl.FreqOffsetHz = requestedControl.TunedFreqHz - requestedControl.CenterFreqHz;

        if (!resetMainViewZoom &&
            centerFrequencyHz != before.CenterFreqHz &&
            requestedControl.BaseMainSpanHz > 0 &&
            requestedControl.MainSpanHz > 0 &&
            requestedControl.MainSpanHz < requestedControl.BaseMainSpanHz)
        {
            resetMainViewZoom = true;
            requestedControl.MainSpanHz = requestedControl.BaseMainSpanHz;
            centerFrequencyHz = request.PreservePreferredCenterFrequency
                ? requestedCenterFrequencyHz
                : TuningCoordinator.RoundInputCenterFrequency(requestedControl);
            requestedControl.CenterFreqHz = (int)centerFrequencyHz;
            requestedControl.FreqOffsetHz = requestedControl.TunedFreqHz - requestedControl.CenterFreqHz;
        }

        long passbandHalfWidth = (long)(sampleRateHz * UsableNyquistRatio);
        long passbandLowerHz = centerFrequencyHz - passbandHalfWidth;
        long passbandUpperHz = centerFrequencyHz + passbandHalfWidth;
        if (lowerEdgeHz < passbandLowerHz || upperEdgeHz > passbandUpperHz)
            return ValueTask.FromResult(SetCurrent(Rejected("The requested targets do not fit in the usable passband."), false));

        RadioControl applied;
        Interlocked.Exchange(ref _isApplyingRequest, 1);
        try
        {
            applied = _controlStore.Update(control =>
            {
                return requestedControl;
            });
        }
        finally
        {
            Volatile.Write(ref _isApplyingRequest, 0);
        }
        _updatePublisher.Publish(applied, resetMainViewZoom);

        bool adjusted = request.PreferredCenterFrequencyHz.HasValue &&
                        request.PreferredCenterFrequencyHz.Value != centerFrequencyHz;
        var result = new PluginTuningResult(
            adjusted ? PluginTuningOutcome.Adjusted : PluginTuningOutcome.Accepted,
            adjusted ? "The host adjusted the requested tuning profile." : "The tuning profile was applied.",
            centerFrequencyHz,
            sampleRateHz,
            passbandLowerHz,
            passbandUpperHz,
            TargetFrequencyHz: requestedControl.TunedFreqHz);
        Volatile.Write(ref _hasAppliedRequest, 1);
        return ValueTask.FromResult(SetCurrent(result, true));
    }

    private void OnRadioControlChanged(object? sender, RadioControlChangedEventArgs args)
    {
        if (Volatile.Read(ref _isApplyingRequest) != 0 ||
            Volatile.Read(ref _hasAppliedRequest) == 0 ||
            !_pluginManager().IsPluginActive(_pluginId) ||
            (args.Previous.CenterFreqHz == args.Current.CenterFreqHz &&
             args.Previous.TunedFreqHz == args.Current.TunedFreqHz &&
             args.Previous.FsHz == args.Current.FsHz))
        {
            return;
        }

        long halfWidth = (long)(args.Current.FsHz * UsableNyquistRatio);
        SetCurrent(new PluginTuningResult(
            PluginTuningOutcome.Adjusted,
            "The applied receiving conditions were changed by the host or user.",
            args.Current.CenterFreqHz,
            args.Current.FsHz,
            args.Current.CenterFreqHz - halfWidth,
            args.Current.CenterFreqHz + halfWidth,
            TargetFrequencyHz: args.Current.TunedFreqHz), true);
    }

    private PluginTuningResult SetCurrent(PluginTuningResult result, bool notifyApplied)
    {
        Volatile.Write(ref _current, result);
        if (notifyApplied) AppliedConfigurationChanged?.Invoke(this, result);
        return result;
    }

    private static bool TryValidate(PluginTuningRequest request, out string error)
    {
        if (string.IsNullOrWhiteSpace(request.ProfileId))
        {
            error = "A tuning profile ID is required.";
            return false;
        }
        if (request.Targets.Count == 0 || request.Targets.Any(target => target.FrequencyHz <= 0 || target.BandwidthHz <= 0))
        {
            error = "At least one valid tuning target is required.";
            return false;
        }
        if (request.MinimumSampleRateHz <= 0)
        {
            error = "The sample-rate requirements are invalid.";
            return false;
        }
        error = string.Empty;
        return true;
    }

    private static long ResolveCenterFrequency(
        PluginTuningRequest request,
        long lowerEdgeHz,
        long upperEdgeHz,
        int sampleRateHz)
    {
        long halfWidth = (long)(sampleRateHz * UsableNyquistRatio);
        long midpointHz = lowerEdgeHz + (upperEdgeHz - lowerEdgeHz) / 2;
        if (!request.PreferredCenterFrequencyHz.HasValue) return midpointHz;
        long preferredHz = request.PreferredCenterFrequencyHz.Value;
        return lowerEdgeHz >= preferredHz - halfWidth && upperEdgeHz <= preferredHz + halfWidth
            ? preferredHz
            : midpointHz;
    }

    private static PluginTuningResult Rejected(string message, int sampleRateHz = 0) => new(
        PluginTuningOutcome.Rejected,
        message,
        0,
        sampleRateHz,
        0,
        0);
}
