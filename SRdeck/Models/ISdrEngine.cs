using System;
using System.Threading.Tasks;
using SRdeckPlugin.Contracts;
using SRdeck.Models;
using SRdeck.Models.SDR;
using SRdeck.DSP;

namespace SRdeck.Models
{
    public interface ISdrEngine : IRadioSessionEngine, IRadioRenderContext, IDisposable
    {
        int RequestedSpectrumWidth { get; set; }

        void SetWorkloadAccelerationPreferences(
            PluginChannelAccelerationPreference light,
            PluginChannelAccelerationPreference standard,
            PluginChannelAccelerationPreference heavy);


        float SystemGainOffset { get; set; }
        float SdrBiasPpm { get; set; }
        int MaxGainReduction { get; }
        int MinGainReduction { get; set; }
        int RfAgcEnabled { get; set; }
        AgcReleaseMode AgcReleaseMode { get; set; }


        double SystemDb { get; set; }

        event Action? StateUpdated;
        event Action? DemodHistoryUpdated;
        event Action? DeviceRemoved;
        event Action? StreamStalled;
        event Action<string?>? OnTitleChanged;

        void InitializeDSP();
        int CurrentGainDb { get; set; }
        void GainChange();
        bool ResidualDcRemovalEnabled { get; set; }
        void ResetResidualDcRemoval();
    }
}
