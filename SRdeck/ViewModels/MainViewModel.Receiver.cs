using CommunityToolkit.Mvvm.ComponentModel;
using SRdeck.Models;

namespace SRdeck.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private void ApplyStep(ref RadioControl parameters, int stepHz, int index)
    {
        parameters.StepHz = stepHz; 
        parameters.ApplyPrimaryReceiverTuning();
    }

    private void ApplySpan(ref RadioControl parameters, int spanHz, int index)
    {
        parameters.SpanHz = spanHz;
    }

    public void ApplyModeDefaults(ref RadioControl parameters, DemodulationMode mode, int index)
    {
        DemodulationMode oldMode = parameters.DemodMode;
        int currentSpan = parameters.SpanHz;

        parameters.DemodMode = mode;

        int stepHz = 25000;
        switch (mode)
        {
            case DemodulationMode.USB: 
            case DemodulationMode.LSB: 
            case DemodulationMode.USB_Wide: 
            case DemodulationMode.LSB_Wide:
                stepHz = 100; break;
            case DemodulationMode.AM_Wide: stepHz = 1000; break;
            case DemodulationMode.FM_Wide: stepHz = 100000; break;
            case DemodulationMode.AM: 
            case DemodulationMode.FM_Narrow: stepHz = 25000; break;
        }

        int spanHz = currentSpan;
        bool wasFmW = oldMode == DemodulationMode.FM_Wide;
        bool isFmW = mode == DemodulationMode.FM_Wide;
        if (spanHz <= 0) spanHz = isFmW ? 250000 : 50000;
        else if (isFmW && !wasFmW) spanHz = 250000;
        else if (!isFmW && wasFmW) spanHz = 50000;

        parameters.StepHz = stepHz; 
        parameters.SpanHz = spanHz;
    }
}
