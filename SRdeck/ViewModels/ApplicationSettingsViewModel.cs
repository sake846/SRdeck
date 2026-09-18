using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using SRdeck.Configuration;
using SRdeck.Models;
using SRdeck.Services;
using SRdeckPlugin.Contracts;

namespace SRdeck.ViewModels;

/// <summary>Owns application preferences and their runtime effects, independently of receiver UI state.</summary>
public partial class ApplicationSettingsViewModel : ObservableObject
{
    private readonly ISdrEngine _engine;
    private readonly ISettingsService _settingsService;
    private readonly ILastStateService _lastStateService;
    private readonly Func<LastState> _lastState;

    public ApplicationSettingsViewModel(ISdrEngine engine, ISettingsService settingsService,
        ILastStateService lastStateService, Func<LastState> lastState)
    {
        _engine = engine;
        _settingsService = settingsService;
        _lastStateService = lastStateService;
        _lastState = lastState;
    }

    public event Action? PowerSettingsChanged;
    public event Action<string>? LanguageResourceChanged;

    [ObservableProperty] private bool _isPreventSleepOnAc;
    [ObservableProperty] private bool _isPreventSleepOnBattery;
    [ObservableProperty] private bool _isDisableWpfRenderingOnServer;
    [ObservableProperty] private bool _isResidualDcRemovalEnabled;
    [ObservableProperty] private SettingsComboBoxOption<PluginChannelAccelerationPreference>? _selectedDemodLightGpu;
    [ObservableProperty] private SettingsComboBoxOption<PluginChannelAccelerationPreference>? _selectedDemodStandardGpu;
    [ObservableProperty] private SettingsComboBoxOption<PluginChannelAccelerationPreference>? _selectedDemodHeavyGpu;
    [ObservableProperty] private SettingsComboBoxOption<string?>? _selectedProcessPriority;
    [ObservableProperty] private SettingsComboBoxOption<string?>? _startupProcessPriority;
    [ObservableProperty] private string _language = "ja";

    partial void OnIsPreventSleepOnAcChanged(bool value)
    {
        if (_engine.InitialAppSettings.Power.PreventSleepOnAc != value)
        {
            _engine.InitialAppSettings.Power.PreventSleepOnAc = value;
            Save();
        }
        PowerSettingsChanged?.Invoke();
    }

    partial void OnIsPreventSleepOnBatteryChanged(bool value)
    {
        if (_engine.InitialAppSettings.Power.PreventSleepOnBattery != value)
        {
            _engine.InitialAppSettings.Power.PreventSleepOnBattery = value;
            Save();
        }
        PowerSettingsChanged?.Invoke();
    }

    partial void OnIsDisableWpfRenderingOnServerChanged(bool value)
    {
        if (_engine.InitialAppSettings.Power.DisableWpfRenderingOnServer == value) return;
        _engine.InitialAppSettings.Power.DisableWpfRenderingOnServer = value;
        Save();
    }

    partial void OnIsResidualDcRemovalEnabledChanged(bool value)
    {
        _engine.ResidualDcRemovalEnabled = value;
        if (_engine.InitialAppSettings.SignalProcessing.ResidualDcRemovalEnabled == value) return;
        _engine.InitialAppSettings.SignalProcessing.ResidualDcRemovalEnabled = value;
        Save();
    }

    partial void OnSelectedDemodLightGpuChanged(SettingsComboBoxOption<PluginChannelAccelerationPreference>? value)
    {
        if (value is null) return;
        bool changed = _engine.InitialAppSettings.Demodulation.LightWorkloadPreference != value.Value;
        _engine.InitialAppSettings.Demodulation.LightWorkloadPreference = value.Value;
        ApplyDemodulationPreferences(changed);
    }

    partial void OnSelectedDemodStandardGpuChanged(SettingsComboBoxOption<PluginChannelAccelerationPreference>? value)
    {
        if (value is null) return;
        bool changed = _engine.InitialAppSettings.Demodulation.StandardWorkloadPreference != value.Value;
        _engine.InitialAppSettings.Demodulation.StandardWorkloadPreference = value.Value;
        ApplyDemodulationPreferences(changed);
    }

    partial void OnSelectedDemodHeavyGpuChanged(SettingsComboBoxOption<PluginChannelAccelerationPreference>? value)
    {
        if (value is null) return;
        bool changed = _engine.InitialAppSettings.Demodulation.HeavyWorkloadPreference != value.Value;
        _engine.InitialAppSettings.Demodulation.HeavyWorkloadPreference = value.Value;
        ApplyDemodulationPreferences(changed);
    }

    private void ApplyDemodulationPreferences(bool changed)
    {
        if (changed) Save();
        DemodulationSettings settings = _engine.InitialAppSettings.Demodulation;
        _engine.SetWorkloadAccelerationPreferences(settings.LightWorkloadPreference,
            settings.StandardWorkloadPreference, settings.HeavyWorkloadPreference);
    }

    partial void OnSelectedProcessPriorityChanged(SettingsComboBoxOption<string?>? value)
    {
        LastState state = _lastState();
        if (state.ProcessPriority != value?.Value)
        {
            state.ProcessPriority = value?.Value;
            _lastStateService.SaveLastState(state);
        }
        ApplyProcessPriority(value?.Value);
    }

    partial void OnStartupProcessPriorityChanged(SettingsComboBoxOption<string?>? value)
    {
        if (_engine.InitialAppSettings.Power.ProcessPriority != value?.Value)
        {
            _engine.InitialAppSettings.Power.ProcessPriority = value?.Value;
            Save();
        }
        if (!string.IsNullOrWhiteSpace(value?.Value)) ApplyProcessPriority(value.Value);
    }

    partial void OnLanguageChanged(string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        if (_engine.InitialAppSettings.Language != value)
        {
            _engine.InitialAppSettings.Language = value;
            Save();
        }
        LanguageResourceChanged?.Invoke(value);
    }

    public void ApplySleepPrevention(bool isActive)
    {
        PowerSettings settings = _engine.InitialAppSettings.Power;
        bool shouldPrevent = isActive && (PowerStateManager.IsAcPowerConnected()
            ? settings.PreventSleepOnAc : settings.PreventSleepOnBattery);
        if (shouldPrevent) PowerStateManager.PreventSleep(true, false);
        else PowerStateManager.RestoreNormalSleep();
    }

    internal static void ApplyProcessPriority(string? priority)
    {
        if (string.IsNullOrWhiteSpace(priority)) return;
        try
        {
            if (Enum.TryParse(priority, ignoreCase: true, out ProcessPriorityClass parsed) && Enum.IsDefined(parsed))
            {
                using Process process = Process.GetCurrentProcess();
                process.PriorityClass = parsed;
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Failed to set process priority: {exception.Message}");
        }
    }

    private void Save() => _settingsService.SaveSettings(_engine.InitialAppSettings);
}
