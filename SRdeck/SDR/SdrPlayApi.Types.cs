using System.Runtime.InteropServices;

namespace SRdeck.SDR;

public static partial class SdrPlayApi
{
    public struct DeviceT
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = SDRPLAY_MAX_SER_NO_LEN)]
        public string SerNo;

        public byte HwVer;

        public TunerSelectT Tuner;

        public RspDuoModeT RspDuoMode;

        public byte Valid;

        public double RspDuoSampleFreq;

        public nint Dev;
    }

    public struct DeviceParamsT
    {
        public nint PDevParams;
        public nint PRxChannelA;
        public nint PRxChannelB;
    }

    public struct sdrplay_api_ErrorInfoT
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string file;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string function;

        public int line;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 1024)]
        public string message;
    }

    public struct GainCbParamT
    {
        public uint GRdB;
        public uint LnaGRdB;
        public double CurrGain;
    }

    public struct PowerOverloadCbParamT
    {
        public PowerOverloadCbEventIdT PoweOverloadChangeType;
    }

    public struct RspDuoModeCbParamT
    {
        public RspDuoModeCbEventIdT ModeChangeType;
    }

    public struct StreamCbParamsT
    {
        public uint FirstSampleNum;
        public int GrChanged;
        public int RfChanged;
        public int FsChanged;
        public uint NumSamples;
    }

    public struct CallbackFnsT
    {
        public StreamCallbackT StreamACbFn;
        public StreamCallbackT StreamBCbFn;
        public EventCallbackT EventCbFn;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct EventParamsT
    {
        [FieldOffset(0)]
        public GainCbParamT GainParams;

        [FieldOffset(0)]
        public PowerOverloadCbParamT PowerOverloadParams;

        [FieldOffset(0)]
        public RspDuoModeCbParamT RspDuoModeParams;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void StreamCallbackT(nint pxi, nint pxq, ref StreamCbParamsT cbparams, uint numSamples, uint reset, nint cbContext);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void EventCallbackT(EventT eventId, TunerSelectT tuner, ref EventParamsT cbparams, nint cbContext);

    public struct DcOffsetT
    {
        public byte DCenable;
        public byte IQenable;
    }

    public struct DecimationT
    {
        public byte Enable;
        public byte DecimationFactor;
        public byte WideBandSignal;
    }

    public struct AgcT
    {
        public AgcControlT Enable;
        public int SetPoint_dBfs;
        public ushort Attack_ms;
        public ushort Decay_ms;
        public ushort Decay_delay_ms;
        public ushort Decay_threshold_dB;
        public int SyncUpdate;
    }

    public struct ControlParamsT
    {
        public DcOffsetT DcOffset;
        public DecimationT Decimation;
        public AgcT Agc;
        public AdsbModeT AbsdMode;
    }

    public struct FsFreqT
    {
        public double FsHz;
        public byte SyncUpdate;
        public byte ReCal;
    }

    public struct SyncUpdateT
    {
        public uint SampleNum;
        public uint Period;
    }

    public struct ResetFlagsT
    {
        public byte ResetGainUpdate;
        public byte ResetRfUpdate;
        public byte ResetFsUpdate;
    }

    public struct DevParamsT
    {
        public double Ppm;
        public FsFreqT FsFreq;
        public SyncUpdateT SyncUpdate;
        public ResetFlagsT ResetFlags;
        public TransferModeT Mode;
        public uint SamplesPerPkt;
        public Rsp1aParamsT Rsp1aParams;
        public Rsp2ParamsT Rsp2Params;
        public RspDuoParamsT RspDuoParams;
        public RspDxParamsT RspDxParams;
    }

    public struct Rsp1aParamsT
    {
        public byte RfNotchEnable;
        public byte DabNotchEnable;
    }

    public struct Rsp1aTunerParamsT
    {
        public byte BiasTEnable;
    }

    public struct Rsp2ParamsT
    {
        public byte ExtRefOutputEn;
    }

    public struct Rsp2TunerParamsT
    {
        public byte BiasTenable;
        public Rsp2_AmPortSelectT AmPortSel;
        public Rsp2_AntennaSelectT AntennaSel;
        public byte RfNotchEnable;
    }

    public struct RspDuoParamsT
    {
        public byte ExtRefOutputEn;
    }

    public struct RspDuo_ResetSlaveFlagsT
    {
        public byte ResetGainUpdate;
        public byte ResetRfUpdate;
    }

    public struct RspDuoTunerParamsT
    {
        public byte BiasTEnable;
        public RspDuo_AmPortSelectT Tuner1AmPortSel;
        public byte Tuner1AmNotchEnable;
        public byte RfNotchEnable;
        public byte RfDabNotchEnable;
        public RspDuo_ResetSlaveFlagsT ResetSlaveFlags;
    }

    public struct RspDxParamsT
    {
        public byte HdrEnable;
        public byte BiasTEnable;
        public RspDx_AntennaSelectT AntennaSel;
        public byte RfNotchEnable;
        public byte DabNotchEnable;
    }

    public struct RspDxTunerParamsT
    {
        public RspDx_HdrModeBwT AntennaSel;
    }

    public struct RxChannelParamsT
    {
        public TunerParamsT TunerParams;
        public ControlParamsT CtrlParams;
        public Rsp1aTunerParamsT Rsp1aTunerParams;
        public Rsp2TunerParamsT Rsp2TunerParams;
        public RspDuoTunerParamsT RspDuoTunerParams;
        public RspDxTunerParamsT RspDxTunerParams;
    }

    public struct GainValuesT
    {
        public float Curr;
        public float Max;
        public float Min;
    }

    public struct GainT
    {
        public int GRdB;
        public byte LNAstate;
        public byte SyncUpdate;
        public MinGainReductionT MinGr;
        public GainValuesT GainVals;
    }

    public struct RfFreqT
    {
        public double RfHz;
        public byte SyncUpdate;
    }

    public struct DcOffsetTunerT
    {
        public byte DcCal;
        public byte SpeedUp;
        public int TrackTime;
        public int RefreshRateTime;
    }

    public struct TunerParamsT
    {
        public Bw_MHzT BwType;
        public If_kHzT IfType;
        public LoModeT LoMode;
        public GainT Gain;
        public RfFreqT RfFreq;
        public DcOffsetTunerT DcOffsetTuner;
    }
}
