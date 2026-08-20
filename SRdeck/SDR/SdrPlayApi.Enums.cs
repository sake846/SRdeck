namespace SRdeck.SDR;

public static partial class SdrPlayApi
{
    public enum ErrT
    {
        Success,
        Fail,
        InvalidParam,
        OutOfRange,
        GainUpdateError,
        RfUpdateError,
        FsUpdateError,
        HwError,
        AliasingError,
        AlreadyInitialised,
        NotInitialised,
        NotEnabled,
        HwVerError,
        OutOfMemError,
        ServiceNotResponding,
        StartPending,
        StopPending,
        InvalidMode,
        FailedVerification1,
        FailedVerification2,
        FailedVerification3,
        FailedVerification4,
        FailedVerification5,
        FailedVerification6,
        InvalidServiceVersion
    }

    public enum DbgLvlT
    {
        Disable,
        Verbose,
        Warning,
        Error,
        Message
    }

    public enum ReasonForUpdateT
    {
        None = 0,
        Update_Dev_Fs = 1,
        Update_Dev_Ppm = 2,
        Update_Dev_SyncUpdate = 4,
        Update_Dev_ResetFlags = 8,
        Update_Rsp1a_BiasTControl = 16,
        Update_Rsp1a_RfNotchControl = 32,
        Update_Rsp1a_RfDabNotchControl = 64,
        Update_Rsp2_BiasTControl = 128,
        Update_Rsp2_AmPortSelect = 256,
        Update_Rsp2_AntennaControl = 512,
        Update_Rsp2_RfNotchControl = 1024,
        Update_Rsp2_ExtRefControl = 2048,
        Update_RspDuo_ExtRefControl = 4096,
        Update_Master_Spare_1 = 8192,
        Update_Master_Spare_2 = 16384,
        Update_Tuner_Gr = 32768,
        Update_Tuner_GrLimits = 65536,
        Update_Tuner_Frf = 131072,
        Update_Tuner_BwType = 262144,
        Update_Tuner_IfType = 524288,
        Update_Tuner_DcOffset = 1048576,
        Update_Tuner_LoMode = 2097152,
        Update_Ctrl_DCoffsetIQimbalance = 4194304,
        Update_Ctrl_Decimation = 8388608,
        Update_Ctrl_Agc = 16777216,
        Update_Ctrl_AdsbMode = 33554432,
        Update_Ctrl_OverloadMsgAck = 67108864,
        Update_RspDuo_BiasTControl = 134217728,
        Update_RspDuo_AmPortSelect = 268435456,
        Update_RspDuo_Tuner1AmNotchControl = 536870912,
        Update_RspDuo_RfNotchControl = 1073741824,
        Update_RspDuo_RfDabNotchControl = int.MinValue
    }

    public enum ReasonForUpdateExtension1T
    {
        None = 0,
        Update_RspDx_HdrEnable = 1,
        Update_RspDx_BiasTControl = 2,
        Update_RspDx_AntennaControl = 4,
        Update_RspDx_RfNotchControl = 8,
        Update_RspDx_RfDabNotchControl = 0x10,
        Update_RspDx_HdrBw = 0x20,
        Update_RspDuo_ResetSlaveFlags = 0x40
    }

    public enum PowerOverloadCbEventIdT
    {
        Overload_Detected,
        Overload_Corrected
    }

    public enum RspDuoModeCbEventIdT
    {
        MasterInitialised,
        SlaveAttached,
        SlaveDetached,
        SlaveInitialised,
        SlaveUninitialised,
        MasterDllDisappeared,
        SlaveDllDisappeared
    }

    public enum EventT
    {
        GainChange,
        PowerOverloadChange,
        DeviceRemoved,
        RspDuoModeChange
    }

    public enum AgcControlT
    {
        AGC_DISABLE,
        AGC_100HZ,
        AGC_50HZ,
        AGC_5HZ,
        AGC_CTRL_EN
    }

    public enum AdsbModeT
    {
        ADSB_DECIMATION,
        ADSB_NO_DECIMATION_LOWPASS,
        ADSB_NO_DECIMATION_BANDPASS_2MHZ,
        ADSB_NO_DECIMATION_BANDPASS_3MHZ
    }

    public enum TransferModeT
    {
        ISOCH,
        BULK
    }

    public enum Rsp2_AntennaSelectT
    {
        ANTENNA_A = 5,
        ANTENNA_B
    }

    public enum Rsp2_AmPortSelectT
    {
        AMPORT_1 = 1,
        AMPORT_2 = 0
    }

    public enum RspDuoModeT
    {
        Unknown = 0,
        Single_Tuner = 1,
        Dual_Tuner = 2,
        Master = 4,
        Slave = 8
    }

    public enum RspDuo_AmPortSelectT
    {
        AMPORT_1 = 1,
        AMPORT_2 = 0
    }

    public enum RspDx_AntennaSelectT
    {
        ANTENNA_A,
        ANTENNA_B,
        ANTENNA_C
    }

    public enum RspDx_HdrModeBwT
    {
        HDRMODE_BW_0_200,
        HDRMODE_BW_0_500,
        HDRMODE_BW_1_200,
        HDRMODE_BW_1_700
    }

    public enum Bw_MHzT
    {
        BW_Undefined = 0,
        BW_0_200 = 200,
        BW_0_300 = 300,
        BW_0_600 = 600,
        BW_1_536 = 1536,
        BW_5_000 = 5000,
        BW_6_000 = 6000,
        BW_7_000 = 7000,
        BW_8_000 = 8000
    }

    public enum If_kHzT
    {
        IF_Undefined = -1,
        IF_Zero = 0,
        IF_0_450 = 450,
        IF_1_620 = 1620,
        IF_2_048 = 2048
    }

    public enum LoModeT
    {
        LO_Undefined,
        LO_Auto,
        LO_120MHz,
        LO_144MHz,
        LO_168MHz
    }

    public enum MinGainReductionT
    {
        EXTENDED_MIN_GR = 0,
        NORMAL_MIN_GR = 20
    }

    public enum TunerSelectT
    {
        Tuner_Neither,
        Tuner_A,
        Tuner_B,
        Tuner_Both
    }
}
