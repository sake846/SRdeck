using System;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using SRdeck.Models;
using SRdeck.Messages;
using SRdeck.Services;


namespace SRdeck.ViewModels;

public partial class MainViewModel : ObservableObject
{
    public void SyncViewModelData(RadioControl radioControl)
    {
        DemodMode = radioControl.DemodMode;
        DemodWaveDisplayMode = radioControl.DemodWaveDisplayMode;
        Tuner.SyncFromCore(radioControl);

        Diagnostics.SyncDiagnostics(radioControl, SelectedLnaState.ToString());
    }

    private void HandleEngineStateUpdated()
    {
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            RestoreActiveDisplayAfterRetune();
            RadioControl radioControl = _engine.Control;
            bool isParamUpdated = false;
            if (RfGainDb != _engine.CurrentGainDb) { RfGainDb = _engine.CurrentGainDb; }

            bool isRtlSdr = IsRtlSdrDeviceController();
            bool isSdrPlay = _engine.SdrDevice is SRdeck.SDR.SdrController;
            if (isSdrPlay)
            {
                int lnaStateCount = Models.SDR.SdrPlayGainPolicy.GetLnaStateCount(
                    GetSdrPlayModelName(),
                    radioControl.CenterFreqHz);
                if (_lastSdrPlayLnaStateCount != lnaStateCount)
                {
                    _lastSdrPlayLnaStateCount = lnaStateCount;
                    if (IsAgcEnabled)
                    {
                        SelectedLnaState = Math.Min(SelectedLnaState, lnaStateCount - 1);
                    }
                    else
                    {
                        ApplySdrPlaySensitivity();
                    }
                }
            }

            if (isSdrPlay && IsAgcEnabled)
            {
                int nominalGr = GetSdrPlayAutomaticNominalGainReduction();
                int upperGr = Math.Min(_engine.MaxGainReduction, nominalGr + 5);
                int lowerGr = Math.Max(_engine.MinGainReduction, nominalGr - 5);

                // SDRplay: attack after 200 ms at the upper GR threshold. Release
                // only after the lower threshold has remained active for 500 ms.
                if (RfGainDb >= upperGr)
                {
                    _gainBelow45StartTime = null;
                    if (_gainAbove55StartTime == null)
                    {
                        _gainAbove55StartTime = DateTime.UtcNow;
                    }
                    else if ((DateTime.UtcNow - _gainAbove55StartTime.Value).TotalMilliseconds >= 200)
                    {
                        if (SelectedLnaState < GetSdrPlayMaxLnaState())
                        {
                            SelectedLnaState++;
                            _engine.CurrentGainDb = nominalGr;
                            if (_engine is Models.CoreEngine core) core.ApplyGainUpdate();
                            RfGainDb = nominalGr;
                            radioControl.RfGainDb = nominalGr;
                            isParamUpdated = true;
                        }
                        _gainAbove55StartTime = null;
                    }
                }
                else if (RfGainDb <= lowerGr)
                {
                    _gainAbove55StartTime = null;
                    if (_gainBelow45StartTime == null)
                    {
                        _gainBelow45StartTime = DateTime.UtcNow;
                    }
                    else if ((DateTime.UtcNow - _gainBelow45StartTime.Value).TotalMilliseconds >= 500)
                    {
                        if (SelectedLnaState > 0)
                        {
                            SelectedLnaState--;
                            _engine.CurrentGainDb = nominalGr;
                            if (_engine is Models.CoreEngine core) core.ApplyGainUpdate();
                            RfGainDb = nominalGr;
                            radioControl.RfGainDb = nominalGr;
                            isParamUpdated = true;
                        }
                        _gainBelow45StartTime = null;
                    }
                }
                else
                {
                    _gainAbove55StartTime = null;
                    _gainBelow45StartTime = null;
                }
            }
            else
            {
                _gainAbove55StartTime = null;
                _gainBelow45StartTime = null;
            }
            if (radioControl.IsBandPlanVisible != IsBandPlanVisible || radioControl.IsStationNameVisible != IsStationNameVisible)
            {
                radioControl.IsBandPlanVisible = IsBandPlanVisible; radioControl.IsStationNameVisible = IsStationNameVisible;
                FrequencyDisplayMode mode = (radioControl.IsBandPlanVisible, radioControl.IsStationNameVisible) switch { (true, true) => FrequencyDisplayMode.Both, (true, false) => FrequencyDisplayMode.BandOnly, (false, true) => FrequencyDisplayMode.StationOnly, _ => FrequencyDisplayMode.None };
                _engine.InitialAppSettings.Display.FrequencyDisplayMode = mode; _settingsService.SaveSettings(_engine.InitialAppSettings);
                isParamUpdated = true;
            }
            if (SyncAutoStep(ref radioControl)) isParamUpdated = true;
            if (_spectrumClickHandler != null && _spectrumClickHandler.IsClicked && _spectrumClickHandler.SyncClickParameters(ref radioControl, SpectrumWidth, IsReceiver1Visible, false, Display.CurrentMainSpanHz)) isParamUpdated = true;
            if (_waterfallClickHandler != null && _waterfallClickHandler.IsClicked && _waterfallClickHandler.SyncClickParameters(ref radioControl, WaterfallWidth, WaterfallHeight, IsReceiver1Visible, false, Display.CurrentMainSpanHz, CurrentWaterfallHistorySeconds)) isParamUpdated = true;
            if (_zoomWindowClickHandler != null && _zoomWindowClickHandler.IsClicked) { _zoomWindowClickHandler.SyncClickParameters(ref radioControl); isParamUpdated = true; }
            if (isParamUpdated) WeakReferenceMessenger.Default.Send(new RadioControlUpdateMessage(radioControl));
            IsBandPlanVisible = radioControl.IsBandPlanVisible; IsStationNameVisible = radioControl.IsStationNameVisible;
            SyncSelectedFrequencyDisplayOption(); SyncState(radioControl, _engine.State);
            UiTick?.Invoke(this, EventArgs.Empty);
        });
    }

    private void HandleEngineDemodHistoryUpdated()
    {
    }

    private bool SyncAutoStep(ref RadioControl radioControl)
    {
        return false;
    }
    private string FormatFilePath(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return filePath ?? "";
        try { string currentDir = AppDomain.CurrentDomain.BaseDirectory; string relPath = System.IO.Path.GetRelativePath(currentDir, filePath); return (relPath.StartsWith("..") || System.IO.Path.IsPathRooted(relPath)) ? filePath : relPath; }
        catch { return filePath; }
    }

}



