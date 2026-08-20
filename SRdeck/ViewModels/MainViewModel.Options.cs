using System;
using System.Windows;
using System.Collections.Generic;
using SRdeckPlugin.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;
using SRdeck.Configuration;
using SRdeck.Models;

namespace SRdeck.ViewModels;

public class FrequencyDisplayOption : ObservableObject
{
    public FrequencyDisplayMode Mode { get; init; }
    public string Label { get; init; } = "";
}

public class ModeConfigOption
{
    public int Value { get; set; }
    public string Label { get; set; } = "";
}

public class SettingsComboBoxOption<T> : ObservableObject
{
    private string _label = "";
    public string Label
    {
        get => _label;
        set => SetProperty(ref _label, value);
    }
    public T Value { get; init; } = default!;
}

public partial class MainViewModel : ObservableObject
{
    public List<ModeConfigOption> ModeConfigOptions { get; } = [];

    public List<ModeConfigOption> ModeConfigOptionsWithNone { get; } = new()
    {
        new() { Value = -1, Label = "なし" },
    };

    // --- UI Combo Box Options ---
    public List<SettingsComboBoxOption<SdrDeviceType>> SdrDeviceTypeOptions { get; } = new()
    {
        new() { Label = "Auto", Value = SdrDeviceType.Auto },
        new() { Label = "SDRplay", Value = SdrDeviceType.SdrPlay },
#if ENABLE_RTLSDR
        new() { Label = "RTL-SDR", Value = SdrDeviceType.RtlSdr },
#endif
#if ENABLE_RX888
        new() { Label = "RX-888 MK2", Value = SdrDeviceType.Rx888Mk2 }
#endif
    };

    public List<SettingsComboBoxOption<float?>> GridTopDbOptions { get; } = new()
    {
        new() { Label = "指定なし", Value = null },
        new() { Label = "0 dB", Value = 0f },
        new() { Label = "-10 dB", Value = -10f },
        new() { Label = "-20 dB", Value = -20f },
        new() { Label = "-30 dB", Value = -30f },
        new() { Label = "-40 dB", Value = -40f },
        new() { Label = "-50 dB", Value = -50f },
        new() { Label = "-60 dB", Value = -60f },
        new() { Label = "-70 dB", Value = -70f },
        new() { Label = "-80 dB", Value = -80f },
        new() { Label = "-90 dB", Value = -90f },
        new() { Label = "-100 dB", Value = -100f }
    };

    public List<SettingsComboBoxOption<int?>> WaterfallColorModeOptions { get; } = new()
    {
        new() { Label = "カラー", Value = 0 }
    };

    public List<SettingsComboBoxOption<int?>> DebugDrawOptions { get; } = new()
    {
        new() { Label = "指定なし", Value = null },
        new() { Label = "On", Value = 1 },
        new() { Label = "Off", Value = 0 }
    };

    public List<SettingsComboBoxOption<FrequencyDisplayMode?>> FrequencyDisplayModeOptions { get; } = new()
    {
        new() { Label = "指定なし", Value = null },
        new() { Label = "バンド・局名両方", Value = FrequencyDisplayMode.Both },
        new() { Label = "バンドのみ", Value = FrequencyDisplayMode.BandOnly },
        new() { Label = "局名のみ", Value = FrequencyDisplayMode.StationOnly },
        new() { Label = "表示なし", Value = FrequencyDisplayMode.None }
    };

    public List<SettingsComboBoxOption<bool?>> IsGpuFftEnabledOptions { get; } = new()
    {
        new() { Label = "On (GPU加速)", Value = true },
        new() { Label = "Off (CPU)", Value = false }
    };

    public List<SettingsComboBoxOption<PluginChannelAccelerationPreference>> DemodChannelAccelerationOptions { get; } = new()
    {
        new() { Label = "自動", Value = PluginChannelAccelerationPreference.Auto },
        new() { Label = "CPU", Value = PluginChannelAccelerationPreference.Cpu },
        new() { Label = "GPU", Value = PluginChannelAccelerationPreference.GpuPreferred }
    };

    public List<SettingsComboBoxOption<int?>> FftResolutionModeOptions { get; } = new()
    {
        new() { Label = "4K", Value = 0 },
        new() { Label = "8K", Value = 1 },
        new() { Label = "16K", Value = 2 },
        new() { Label = "32K", Value = 3 },
        new() { Label = "64K", Value = 4 },
        new() { Label = "128K", Value = 5 },
        new() { Label = "256K", Value = 6 },
        new() { Label = "512K", Value = 7 },
        new() { Label = "1M", Value = 8 },
        new() { Label = "2M", Value = 9 },
        new() { Label = "4M", Value = 10 }
    };

    public List<SettingsComboBoxOption<int?>> FftBatchModeOptions { get; } = new()
    {
        new() { Label = "指定なし", Value = null },
        new() { Label = "1回", Value = 0 },
        new() { Label = "2回", Value = 1 },
        new() { Label = "4回", Value = 2 },
        new() { Label = "8回", Value = 3 },
        new() { Label = "16回", Value = 4 },
        new() { Label = "32回", Value = 5 }
    };

    public List<SettingsComboBoxOption<bool?>> StartServerOptions { get; } = new()
    {
        new() { Label = "指定なし", Value = null },
        new() { Label = "On", Value = true },
        new() { Label = "Off", Value = false }
    };

    public List<SettingsComboBoxOption<bool?>> IsAudioRemoteOptionOptions { get; } = new()
    {
        new() { Label = "指定なし", Value = null },
        new() { Label = "On", Value = true },
        new() { Label = "Off", Value = false }
    };

    public List<SettingsComboBoxOption<bool?>> IsDisableWpfRenderingOptions { get; } = new()
    {
        new() { Label = "指定なし", Value = null },
        new() { Label = "描画停止", Value = true },
        new() { Label = "通常描画", Value = false }
    };
    
    public List<SettingsComboBoxOption<string?>> ProcessPriorityOptions { get; } = new()
    {
        new() { Label = "Normal (通常)", Value = "Normal" },
        new() { Label = "Above Normal (通常以上)", Value = "AboveNormal" },
        new() { Label = "High (高)", Value = "High" }
    };
    
    public List<SettingsComboBoxOption<string?>> StartupProcessPriorityOptions { get; } = new()
    {
        new() { Label = "指定なし", Value = null },
        new() { Label = "Normal (通常)", Value = "Normal" },
        new() { Label = "Above Normal (通常以上)", Value = "AboveNormal" },
        new() { Label = "High (高)", Value = "High" }
    };

    // --- Language & Label Management Methods ---
    private void FlattenJson(string key, System.Text.Json.JsonElement element, Dictionary<string, string> target)
    {
        if (element.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                FlattenJson(property.Name, property.Value, target);
            }
        }
        else if (element.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            target[key] = element.GetString() ?? "";
        }
        else if (element.ValueKind == System.Text.Json.JsonValueKind.Number ||
                 element.ValueKind == System.Text.Json.JsonValueKind.True ||
                 element.ValueKind == System.Text.Json.JsonValueKind.False)
        {
            target[key] = element.ToString();
        }
    }

    private void SyncWpfLanguageResource(string language)
    {
        try
        {
            var app = Application.Current;
            if (app == null) return;

            ResourceDictionary? oldStrings = null;
            foreach (var dictionary in app.Resources.MergedDictionaries)
            {
                if (dictionary.Contains("__IsLanguageDict"))
                {
                    oldStrings = dictionary;
                    break;
                }
            }

            var assembly = typeof(MainViewModel).Assembly;
            var resourceName = $"SRdeck.Strings.Strings_{language}.json";
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                System.Diagnostics.Debug.WriteLine($"Resource {resourceName} not found.");
                return;
            }

            using var reader = new System.IO.StreamReader(stream);
            var jsonString = reader.ReadToEnd();
            using var doc = System.Text.Json.JsonDocument.Parse(jsonString);

            var flatDict = new Dictionary<string, string>();
            FlattenJson("", doc.RootElement, flatDict);

            var newDict = new ResourceDictionary();
            newDict["__IsLanguageDict"] = "True";
            foreach (var keyValuePair in flatDict)
            {
                newDict[keyValuePair.Key] = keyValuePair.Value;
            }

            if (oldStrings != null)
            {
                int index = app.Resources.MergedDictionaries.IndexOf(oldStrings);
                app.Resources.MergedDictionaries[index] = newDict;
            }
            else
            {
                app.Resources.MergedDictionaries.Add(newDict);
            }

            // FFTアベレージングオプションのラベルを日英に動的更新
            SyncWpfFftBatchLabels(language);
            SyncWpfComboBoxLabels(language);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to switch language: {ex.Message}");
        }
    }

    public Dictionary<string, string> GetCurrentTranslations()
    {
        var dict = new Dictionary<string, string>();
        try
        {
            var app = Application.Current;
            if (app != null)
            {
                foreach (var mergedDictionary in app.Resources.MergedDictionaries)
                {
                    if (mergedDictionary.Contains("__IsLanguageDict"))
                    {
                        foreach (System.Collections.DictionaryEntry entry in mergedDictionary)
                        {
                            if (entry.Key is string key && entry.Value is string value && key != "__IsLanguageDict")
                            {
                                dict[key] = value;
                            }
                        }
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to get translations: {ex.Message}");
        }
        return dict;
    }

    private void SyncWpfFftBatchLabels(string language)
    {
        if (FftBatchOptions == null || FftBatchOptions.Count == 0) return;
        string suffix = language == "en" ? " Time" : "回";
        string pluralSuffix = language == "en" ? " Times" : "回";
        foreach (var option in FftBatchOptions)
        {
            option.Label = option.Count == 1 ? $"1{suffix}" : $"{option.Count}{pluralSuffix}";
        }
    }

    private void SyncWpfComboBoxLabels(string language)
    {
        string notSpecified = language == "en" ? "Not Specified" : "指定なし";
        string both = language == "en" ? "Both" : "バンド・局名両方";
        string bandOnly = language == "en" ? "Band Only" : "バンドのみ";
        string stationOnly = language == "en" ? "Station Only" : "局名のみ";
        string none = language == "en" ? "None" : "表示なし";
        string color = language == "en" ? "Color" : "カラー";
        string green = language == "en" ? "Green" : "グリーン";
        string amber = language == "en" ? "Amber" : "アンバー";
        string gpuOn = language == "en" ? "On (GPU)" : "On (GPU加速)";
        string gpuOff = language == "en" ? "Off (CPU)" : "Off (CPU)";
        string renderStop = language == "en" ? "Disable Rendering" : "描画停止";
        string renderNormal = language == "en" ? "Normal Rendering" : "通常描画";
        string on = language == "en" ? "On" : "On";
        string off = language == "en" ? "Off" : "Off";

        if (GridTopDbOptions != null)
        {
            foreach (var option in GridTopDbOptions)
            {
                if (option.Value == null) option.Label = notSpecified;
            }
        }
        
        if (WaterfallColorModeOptions != null)
        {
            foreach (var option in WaterfallColorModeOptions)
            {
                if (option.Value == null) option.Label = notSpecified;
                else if (option.Value == 0) option.Label = color;
                else if (option.Value == 1) option.Label = green;
                else if (option.Value == 2) option.Label = amber;
            }
        }

        if (DebugDrawOptions != null)
        {
            foreach (var option in DebugDrawOptions)
            {
                if (option.Value == null) option.Label = notSpecified;
                else if (option.Value == 1) option.Label = on;
                else if (option.Value == 0) option.Label = off;
            }
        }

        if (FrequencyDisplayModeOptions != null)
        {
            foreach (var option in FrequencyDisplayModeOptions)
            {
                if (option.Value == null) option.Label = notSpecified;
                else if (option.Value == FrequencyDisplayMode.Both) option.Label = both;
                else if (option.Value == FrequencyDisplayMode.BandOnly) option.Label = bandOnly;
                else if (option.Value == FrequencyDisplayMode.StationOnly) option.Label = stationOnly;
                else if (option.Value == FrequencyDisplayMode.None) option.Label = none;
            }
        }

        if (IsGpuFftEnabledOptions != null)
        {
            foreach (var option in IsGpuFftEnabledOptions)
            {
                if (option.Value == null) option.Label = notSpecified;
                else if (option.Value == true) option.Label = gpuOn;
                else if (option.Value == false) option.Label = gpuOff;
            }
        }

        if (FftResolutionModeOptions != null)
        {
            foreach (var option in FftResolutionModeOptions)
            {
                if (option.Value == null) option.Label = notSpecified;
            }
        }

        if (FftBatchModeOptions != null)
        {
            foreach (var option in FftBatchModeOptions)
            {
                if (option.Value == null) option.Label = notSpecified;
                else if (option.Value == 0) option.Label = language == "en" ? "1 Time" : "1回";
                else if (option.Value == 1) option.Label = language == "en" ? "2 Times" : "2回";
                else if (option.Value == 2) option.Label = language == "en" ? "4 Times" : "4回";
                else if (option.Value == 3) option.Label = language == "en" ? "8 Times" : "8回";
                else if (option.Value == 4) option.Label = language == "en" ? "16 Times" : "16回";
                else if (option.Value == 5) option.Label = language == "en" ? "32 Times" : "32回";
            }
        }

        if (StartServerOptions != null)
        {
            foreach (var option in StartServerOptions)
            {
                if (option.Value == null) option.Label = notSpecified;
                else if (option.Value == true) option.Label = on;
                else if (option.Value == false) option.Label = off;
            }
        }

        if (IsAudioRemoteOptionOptions != null)
        {
            foreach (var option in IsAudioRemoteOptionOptions)
            {
                if (option.Value == null) option.Label = notSpecified;
                else if (option.Value == true) option.Label = on;
                else if (option.Value == false) option.Label = off;
            }
        }

        if (IsDisableWpfRenderingOptions != null)
        {
            foreach (var option in IsDisableWpfRenderingOptions)
            {
                if (option.Value == null) option.Label = notSpecified;
                else if (option.Value == true) option.Label = renderStop;
                else if (option.Value == false) option.Label = renderNormal;
            }
        }

        if (ProcessPriorityOptions != null)
        {
            foreach (var option in ProcessPriorityOptions)
            {
                if (option.Value == null) option.Label = notSpecified;
                else if (option.Value == "Normal") option.Label = language == "en" ? "Normal" : "Normal (通常)";
                else if (option.Value == "AboveNormal") option.Label = language == "en" ? "Above Normal" : "Above Normal (通常以上)";
                else if (option.Value == "High") option.Label = language == "en" ? "High" : "High (高)";
                else if (option.Value == "RealTime") option.Label = language == "en" ? "Realtime (Extreme)" : "Realtime (リアルタイム)";
            }
        }
        
        if (StartupProcessPriorityOptions != null)
        {
            foreach (var option in StartupProcessPriorityOptions)
            {
                if (option.Value == null) option.Label = notSpecified;
                else if (option.Value == "Normal") option.Label = language == "en" ? "Normal" : "Normal (通常)";
                else if (option.Value == "AboveNormal") option.Label = language == "en" ? "Above Normal" : "Above Normal (通常以上)";
                else if (option.Value == "High") option.Label = language == "en" ? "High" : "High (高)";
                else if (option.Value == "RealTime") option.Label = language == "en" ? "Realtime (Extreme)" : "Realtime (リアルタイム)";
            }
        }

        if (ModeConfigOptionsWithNone != null)
        {
            var noneOpt = ModeConfigOptionsWithNone.Find(o => o.Value == -1);
            if (noneOpt != null)
            {
                noneOpt.Label = language == "en" ? "None" : "なし";
            }
        }
    }
}
