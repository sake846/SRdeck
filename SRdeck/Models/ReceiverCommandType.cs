namespace SRdeck.Models
{
    public enum ReceiverCommandType
    {
        None = 0,
        PowerToggle,
        MuteToggle,
        SquelchToggle,
        SquelchDown,
        SquelchUp,
        DelayReset,
        StationDialog,
        
        Span5k,
        Span10k,
        Span20k,
        Span50k,

        DemodCw,
        DemodCwR,
        DemodUsb,
        DemodLsb,
        DemodAmN,
        DemodAmW,
        DemodFmN,
        DemodFmW,

        Step10,
        Step100,
        Step500,
        Step1k,
        Step5k,
        Step6_25k,
        Step8_33k,
        Step9k,
        Step10k,
        Step12_5k,
        Step15k,
        Step20k,
        Step25k,
        Step30k,
        Step50k,
        Step100k
    }
}
