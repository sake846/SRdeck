using System.Collections.Generic;
using SRdeckPlugin.Contracts;
using SRdeck.Models;

namespace SRdeck.Configuration;

public enum SdrDeviceType
{
    SdrPlay,
    RtlSdr,
    Auto
}

public class AppSettings
{
    public DisplaySettings Display { get; set; } = new();
    public PowerSettings Power { get; set; } = new();
    public PluginSelectionSettings Plugins { get; set; } = new();
    public SignalProcessingSettings SignalProcessing { get; set; } = new();
    public DemodulationSettings Demodulation { get; set; } = new();
    public string Language { get; set; } = "ja";
    public SdrDeviceType SdrDeviceType { get; set; } = SdrDeviceType.Auto;
    public int SdrPlaySampleRateHz { get; set; } = 8000000;
    private List<ModeButtonConfig>? _modeButtons;
    public List<ModeButtonConfig> ModeButtons
    {
        get => _modeButtons ??= GetDefaultModeButtons();
        set => _modeButtons = value;
    }

    private static List<ModeButtonConfig> GetDefaultModeButtons()
    {
        return new List<ModeButtonConfig>
        {
            new() { DefaultLabel = "", Mode1 = -1, Mode2 = -1, Mode3 = -1 },
            new() { DefaultLabel = "", Mode1 = -1, Mode2 = -1, Mode3 = -1 },
            new() { DefaultLabel = "", Mode1 = -1, Mode2 = -1, Mode3 = -1 },
            new() { DefaultLabel = "", Mode1 = -1, Mode2 = -1, Mode3 = -1 },
            new() { DefaultLabel = "", Mode1 = -1, Mode2 = -1, Mode3 = -1 },
            new() { DefaultLabel = "", Mode1 = -1, Mode2 = -1, Mode3 = -1 }
        };
    }
}

public class PluginSelectionSettings
{
    public string? SelectedPluginId { get; set; }
}

public class PowerSettings
{
    public bool PreventSleepOnAc { get; set; } = true;
    public bool PreventSleepOnBattery { get; set; } = false;
    public bool DisableWpfRenderingOnServer { get; set; } = true;
    public string? ProcessPriority { get; set; } = "Normal";
}

public class DisplaySettings
{
    public int? WaterfallColorMode { get; set; } = null;
    public int? DebugDraw { get; set; } = null;
    public FrequencyDisplayMode? FrequencyDisplayMode { get; set; } = null;
    public bool IsGpuFftEnabled { get; set; } = true;
    public int FftResolutionMode { get; set; } = 1; // Default 8K
    public float? GridTopDb { get; set; } = null;
}

public class SignalProcessingSettings
{
    public bool ResidualDcRemovalEnabled { get; set; } = false;
}

public class ModeButtonConfig
{
    public string DefaultLabel { get; set; } = "";
    public int Mode1 { get; set; } = -1;
    public int Mode2 { get; set; } = -1;
    public int Mode3 { get; set; } = -1;
}

public class DemodulationSettings
{
    public PluginChannelAccelerationPreference LightWorkloadPreference { get; set; } = PluginChannelAccelerationPreference.Auto;
    public PluginChannelAccelerationPreference StandardWorkloadPreference { get; set; } = PluginChannelAccelerationPreference.Auto;
    public PluginChannelAccelerationPreference HeavyWorkloadPreference { get; set; } = PluginChannelAccelerationPreference.Auto;
}
