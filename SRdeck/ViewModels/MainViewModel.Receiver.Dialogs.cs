using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using SRdeck.Models;
using SRdeck.Messages;
using SRdeck.Views;

namespace SRdeck.ViewModels;

public partial class MainViewModel : ObservableObject
{
    [RelayCommand]
    private void OpenFrequencyInputDialog()
    {
        ResetMouseInteraction();
        var dialog = new FrequencyInputDialog() { 
            Owner = Application.Current.MainWindow,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ReceiverName = "Receiver 1",
            ThemeBrush = (System.Windows.Media.Brush)Application.Current.FindResource("LedBlueBrush")
        };
        if (dialog.ShowDialog() == true) ApplyFrequencyInput(dialog.ResultFrequencyHz, 1);
        ResetMouseInteraction();
    }

    private void ResetMouseInteraction()
    {
        _isSpectrumDragging = false;
        _isWaterfallDraggingCenter = false;
        _isWaterfallDraggingFrame = false;
        _isZoomDragging = false;
    }

    private void ApplyFrequencyInput(long newFrequencyHz, int index)
    {
        ResetMouseInteraction();
        SyncMainSpanForAtomicViewUpdate(Display.BaseMainSpanHz);

        RadioControl radioControl = _engine.Control;
        radioControl.MainSpanHz = Display.CurrentMainSpanHz;
        radioControl.BaseMainSpanHz = Display.BaseMainSpanHz;
        long rounding = Display.CurrentMainRoundingHz;
        long centerFreqHz = ((newFrequencyHz + (rounding / 2)) / rounding) * rounding;
        radioControl.CenterFreqHz = (int)centerFreqHz;

        radioControl.TunedFreqHz = (int)newFrequencyHz;
        radioControl.FreqOffsetHz = radioControl.TunedFreqHz - radioControl.CenterFreqHz;
        radioControl.HistorySec = 0; 
        radioControl.IsPowerOn = true; 
        radioControl.IsZoomWindowVisible = true; 
        radioControl.IsSpeakerOn = true;

        radioControl.ApplyPrimaryReceiverAfcTuning();

        _engine.Control = radioControl;
        WeakReferenceMessenger.Default.Send(new RadioControlUpdateMessage(radioControl));

        Tuner.BuildCenterFreqDigits(); 
        Tuner.TunedFreqHz = radioControl.TunedFreqHz; 
        Tuner.HistorySec = radioControl.HistorySec;

        if (SyncAutoStep(ref radioControl))
        {
            _engine.Control = radioControl;
            WeakReferenceMessenger.Default.Send(new RadioControlUpdateMessage(radioControl));
        }
    }
}
