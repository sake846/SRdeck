using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using SRdeck.Models;
using SRdeck.Messages;

namespace SRdeck.ViewModels;

public partial class MainViewModel : ObservableObject
{
    [RelayCommand]
    private void ToggleReceiver1Visibility() => IsReceiver1Visible = true;

    [RelayCommand]
    private void ReceiverButtonClick(object commandObject)
    {
        if (commandObject != null && Enum.TryParse<ReceiverCommandType>(commandObject.ToString(), out var commandType))
        {
            RadioControl radioControl = _engine.Control;
            ApplyReceiverCommand(commandType, ref radioControl);
            WeakReferenceMessenger.Default.Send(new RadioControlUpdateMessage(radioControl));
        }
    }

    internal void ApplyReceiverCommand(ReceiverCommandType commandType, ref RadioControl parameters)
    {
        switch (commandType)
        {
            case ReceiverCommandType.PowerToggle:
                parameters.IsPowerOn = !parameters.IsPowerOn;
                parameters.IsZoomWindowVisible = parameters.IsPowerOn;
                if (parameters.IsPowerOn)
                {
                    parameters.FreqOffsetHz = 0;
                    parameters.ApplyPrimaryReceiverTuning();
                    parameters.HistorySec = 0;
                    parameters.IsSpeakerOn = true;
                }
                else parameters.IsSpeakerOn = false;
                break;
            case ReceiverCommandType.MuteToggle: 
                break;
            case ReceiverCommandType.SquelchToggle: 
                break;
            case ReceiverCommandType.SquelchDown: 
                break;
            case ReceiverCommandType.SquelchUp: 
                break;
            case ReceiverCommandType.DelayReset: 
                parameters.HistorySec = 0;
                break;

            case ReceiverCommandType.Span5k: ApplySpan(ref parameters, 5000, 1); break;
            case ReceiverCommandType.Span10k: ApplySpan(ref parameters, 10000, 1); break;
            case ReceiverCommandType.Span20k: ApplySpan(ref parameters, 20000, 1); break;
            case ReceiverCommandType.Span50k: ApplySpan(ref parameters, 50000, 1); break;
            case ReceiverCommandType.DemodCw: 
            case ReceiverCommandType.DemodCwR: 
            case ReceiverCommandType.DemodUsb: 
            case ReceiverCommandType.DemodLsb: 
            case ReceiverCommandType.DemodAmN: 
            case ReceiverCommandType.DemodAmW: 
            case ReceiverCommandType.DemodFmN: 
            case ReceiverCommandType.DemodFmW: 
                ApplyModeDefaults(ref parameters, GetDemodModeFromCommand(commandType), 1);
                break;
            case ReceiverCommandType.Step10: ApplyStep(ref parameters, 10, 1); break;
            case ReceiverCommandType.Step100: ApplyStep(ref parameters, 100, 1); break;
            case ReceiverCommandType.Step500: ApplyStep(ref parameters, 500, 1); break;
            case ReceiverCommandType.Step1k: ApplyStep(ref parameters, 1000, 1); break;
            case ReceiverCommandType.Step5k: ApplyStep(ref parameters, 5000, 1); break;
            case ReceiverCommandType.Step6_25k: ApplyStep(ref parameters, 6250, 1); break;
            case ReceiverCommandType.Step8_33k: ApplyStep(ref parameters, 8333, 1); break;
            case ReceiverCommandType.Step9k: ApplyStep(ref parameters, 9000, 1); break;
            case ReceiverCommandType.Step10k: ApplyStep(ref parameters, 10000, 1); break;
            case ReceiverCommandType.Step12_5k: ApplyStep(ref parameters, 12500, 1); break;
            case ReceiverCommandType.Step15k: ApplyStep(ref parameters, 15000, 1); break;
            case ReceiverCommandType.Step20k: ApplyStep(ref parameters, 20000, 1); break;
            case ReceiverCommandType.Step25k: ApplyStep(ref parameters, 25000, 1); break;
            case ReceiverCommandType.Step30k: ApplyStep(ref parameters, 30000, 1); break;
            case ReceiverCommandType.Step50k: ApplyStep(ref parameters, 50000, 1); break;
            case ReceiverCommandType.Step100k: ApplyStep(ref parameters, 100000, 1); break;
        }
    }

    private DemodulationMode GetDemodModeFromCommand(ReceiverCommandType commandType)
    {
        return commandType switch
        {
            ReceiverCommandType.DemodCw => DemodulationMode.USB,
            ReceiverCommandType.DemodCwR => DemodulationMode.LSB,
            ReceiverCommandType.DemodUsb => DemodulationMode.USB_Wide,
            ReceiverCommandType.DemodLsb => DemodulationMode.LSB_Wide,
            ReceiverCommandType.DemodAmN => DemodulationMode.AM,
            ReceiverCommandType.DemodAmW => DemodulationMode.AM_Wide,
            ReceiverCommandType.DemodFmN => DemodulationMode.FM_Narrow,
            ReceiverCommandType.DemodFmW => DemodulationMode.FM_Wide,
            _ => DemodulationMode.AM
        };
    }
}


