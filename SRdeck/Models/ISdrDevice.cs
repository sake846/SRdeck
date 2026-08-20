using System;

namespace SRdeck.Models
{
    public interface ISdrDevice : IDisposable
    {
        SdrDeviceCapabilities Capabilities { get; }
        int FsHz { get; set; }
        long CenterFreqHz { get; set; }
        int MaxGainReduction { get; }
        int RfGainDb { get; set; }
        bool RfAgcEnabled { get; set; }
        float PpmAdjustment { get; set; }
        float BiasPpm { get; set; }

        int LnaState { get; set; }
        int NotchFilterMode { get; set; } // 0: Off, 1: MW+FM, 2: DAB, 3: Both

        event Action<short[], short[], uint> SamplesReceived;
        event Action<double, int> GainHardwareChanged;
        event Action DeviceRemoved;
        event Action StreamStalled;

        bool Open();
        bool Start();
        void Stop();
        void GainChange();
        void FreqChange();
        void ApplyLnaAndNotch();
    }
}
