using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SRdeck.Configuration;
using SRdeck.Views;
using SRdeckPlugin.Contracts;

namespace SRdeck.ViewModels;

public partial class MainViewModel : ObservableObject
{
    [RelayCommand]
    private Task OpenStationCatalog() => OpenFrequencyCatalogAsync(showBandPlans: false);

    [RelayCommand]
    private Task OpenBandPlanCatalog() => OpenFrequencyCatalogAsync(showBandPlans: true);

    private async Task OpenFrequencyCatalogAsync(bool showBandPlans)
    {
        long currentFrequencyHz = _pluginManager.TryGetActiveCapability<IPluginFrequencySelection>(
            out IPluginFrequencySelection? selector) && selector is not null
            ? selector.SelectedFrequencyHz
            : _engine.Control.TunedFreqHz;
        var dialog = new FrequencyCatalogDialog(
            _frequencyCatalog,
            currentFrequencyHz,
            showBandPlans)
        {
            Owner = Application.Current?.MainWindow
        };

        bool tune = dialog.ShowDialog() == true && dialog.ResultFrequencyHz is long;
        ReloadFrequencyCatalogs();
        if (!tune) return;

        if (!_pluginManager.TryGetActiveCapability<IPluginFrequencySelection>(out selector) || selector is null)
        {
            _dialogService.ShowMessage(
                "現在のプラグインは局・バンドプランからの選局に対応していません。アナログ復調プラグインを選択してください。",
                "選局できません");
            return;
        }

        try
        {
            PluginTuningResult result = await selector.SelectFrequencyAsync(dialog.ResultFrequencyHz!.Value);
            if (result.Outcome is PluginTuningOutcome.Rejected or PluginTuningOutcome.Deferred)
                _dialogService.ShowMessage(result.Message, "選局できません");
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            _dialogService.ShowMessage(exception.Message, "選局できません");
        }
    }

    private void ReloadFrequencyCatalogs()
    {
        SpectrumOverlay.LoadStationNames(_frequencyCatalog.LoadStations());
        SpectrumOverlay.LoadBandPlans(_frequencyCatalog.LoadBandPlans());
    }
}
