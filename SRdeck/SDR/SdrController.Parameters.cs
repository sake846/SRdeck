using System;
using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using SRdeck.Messages;
using SRdeck.Models.SDR;

namespace SRdeck.SDR;

public partial class SdrController
{
    private bool RefreshDeviceParameters()
    {
        SdrPlayApi.ErrT err = SdrPlayApi.sdrplay_api_GetDeviceParams(_devices[0].Dev, ref _pdeviceParams);
        if (err != SdrPlayApi.ErrT.Success)
        {
            HandleSdrError("SdrPlayApi.sdrplay_api_GetDeviceParams failed", err);
            return false;
        }
        if (_pdeviceParams == IntPtr.Zero)
        {
            if (!SuppressErrors) WeakReferenceMessenger.Default.Send(new SdrErrorMessage("SdrPlayApi.sdrplay_api_GetDeviceParams returned NULL pointer."));
            return false;
        }
        
        _deviceParams = Marshal.PtrToStructure<SdrPlayApi.DeviceParamsT>(_pdeviceParams);
        _devParams = Marshal.PtrToStructure<SdrPlayApi.DevParamsT>(_deviceParams.PDevParams);
        _rxChannelParamsA = Marshal.PtrToStructure<SdrPlayApi.RxChannelParamsT>(_deviceParams.PRxChannelA);
        return true;
    }

    private void SyncDeviceParameters()
    {
        SyncDeviceParametersBlock();
        SyncTunerParameters();
    }

    private static (double hardwareFsHz, byte decimationFactor) ResolveHardwareSampleRateAndDecimation(int targetFsHz)
    {
        if (targetFsHz <= 0) return (2_000_000.0, 1);

        if (targetFsHz == 1_600_000)
        {
            return (6_400_000.0, 4);
        }

        if (targetFsHz < 2_000_000)
        {
            return (2_000_000.0, 1);
        }

        if (targetFsHz > 10_000_000)
        {
            return (10_000_000.0, 1);
        }

        return (targetFsHz, 1);
    }

    private void SyncDeviceParametersBlock()
    {
        if (_deviceParams.PDevParams == IntPtr.Zero) return;

        var (hardwareFsHz, _) = ResolveHardwareSampleRateAndDecimation(FsHz);

        _devParams.FsFreq.FsHz = hardwareFsHz;
        _devParams.Ppm = BiasPpm;
        Marshal.StructureToPtr(_devParams, _deviceParams.PDevParams, fDeleteOld: false);
    }

    private void SyncTunerParameters()
    {
        if (_deviceParams.PRxChannelA == IntPtr.Zero) return;

        _rxChannelParamsA.TunerParams.RfFreq.RfHz = CalculateAdjustedFrequency();

        var (hardwareFsHz, decimationFactor) = ResolveHardwareSampleRateAndDecimation(FsHz);

        _rxChannelParamsA.TunerParams.BwType = GetBandwidthType(FsHz);
        _rxChannelParamsA.TunerParams.IfType = SdrPlayApi.If_kHzT.IF_Zero;
        
        _rxChannelParamsA.TunerParams.Gain.MinGr = SdrPlayApi.MinGainReductionT.EXTENDED_MIN_GR;
        _rxChannelParamsA.TunerParams.Gain.GRdB = RfGainDb;
        int requestedLnaState = LnaState;
        int normalizedLnaState = SdrPlayGainPolicy.ClampLnaState(ModelName, CenterFreqHz, requestedLnaState);
        if (normalizedLnaState != requestedLnaState)
        {
            LnaState = normalizedLnaState;
            SdrPlayDiagnosticLog.Write(
                "lna-state-clamped",
                $"model={ModelName} frequency={CenterFreqHz} requested={requestedLnaState} applied={normalizedLnaState}");
        }
        _rxChannelParamsA.TunerParams.Gain.LNAstate = (byte)normalizedLnaState;

        _rxChannelParamsA.CtrlParams.Decimation.Enable = decimationFactor > 1 ? (byte)1 : (byte)0;
        _rxChannelParamsA.CtrlParams.Decimation.DecimationFactor = decimationFactor;

        SyncModelSpecificParameters();

        // Gain is controlled by the host-side AGC. Never enable SDRplay AGC.
        _rxChannelParamsA.CtrlParams.Agc.Enable = SdrPlayApi.AgcControlT.AGC_DISABLE;
        Marshal.StructureToPtr(_rxChannelParamsA, _deviceParams.PRxChannelA, fDeleteOld: false);
    }

    private void SyncModelSpecificParameters()
    {
        if (_deviceParams.PDevParams != IntPtr.Zero)
        {
            byte hwVer = _devices[0].HwVer;

            switch (hwVer)
            {
                case SdrPlayApi.SDRPLAY_RSP1A_ID:
                    _rxChannelParamsA.Rsp1aTunerParams.BiasTEnable = ToByte(BiasTEnabled);
                    _devParams.Rsp1aParams.RfNotchEnable = ToByte(IsBroadcastNotchEnabled);
                    _devParams.Rsp1aParams.DabNotchEnable = ToByte(IsDabNotchEnabled);
                    break;

                case SdrPlayApi.SDRPLAY_RSP1B_ID:
                    _rxChannelParamsA.Rsp1aTunerParams.BiasTEnable = ToByte(BiasTEnabled);
                    _devParams.Rsp1aParams.RfNotchEnable = ToByte(IsBroadcastNotchEnabled);
                    _devParams.Rsp1aParams.DabNotchEnable = ToByte(IsDabNotchEnabled);
                    break;

                case SdrPlayApi.SDRPLAY_RSP2_ID:
                    _devParams.Rsp2Params.ExtRefOutputEn = ToByte(ExternalReferenceOutputEnabled);
                    _rxChannelParamsA.Rsp2TunerParams.BiasTenable = ToByte(BiasTEnabled);
                    _rxChannelParamsA.Rsp2TunerParams.AmPortSel = AmPortIndex == 0
                        ? SdrPlayApi.Rsp2_AmPortSelectT.AMPORT_1
                        : SdrPlayApi.Rsp2_AmPortSelectT.AMPORT_2;
                    _rxChannelParamsA.Rsp2TunerParams.AntennaSel = AntennaIndex == 1
                        ? SdrPlayApi.Rsp2_AntennaSelectT.ANTENNA_B
                        : SdrPlayApi.Rsp2_AntennaSelectT.ANTENNA_A;
                    _rxChannelParamsA.Rsp2TunerParams.RfNotchEnable = ToByte(IsBroadcastNotchEnabled);
                    break;

                case SdrPlayApi.SDRPLAY_RSPduo_ID:
                    _devParams.RspDuoParams.ExtRefOutputEn = ToByte(ExternalReferenceOutputEnabled);
                    _rxChannelParamsA.RspDuoTunerParams.BiasTEnable = ToByte(BiasTEnabled);
                    _rxChannelParamsA.RspDuoTunerParams.Tuner1AmPortSel = AmPortIndex == 0
                        ? SdrPlayApi.RspDuo_AmPortSelectT.AMPORT_1
                        : SdrPlayApi.RspDuo_AmPortSelectT.AMPORT_2;
                    _rxChannelParamsA.RspDuoTunerParams.Tuner1AmNotchEnable = ToByte(IsBroadcastNotchEnabled);
                    _rxChannelParamsA.RspDuoTunerParams.RfNotchEnable = ToByte(IsBroadcastNotchEnabled);
                    _rxChannelParamsA.RspDuoTunerParams.RfDabNotchEnable = ToByte(IsDabNotchEnabled);
                    break;

                case SdrPlayApi.SDRPLAY_RSPdx_ID:
                case SdrPlayApi.SDRPLAY_RSPdxR2_ID:
                    _devParams.RspDxParams.HdrEnable = ToByte(HdrEnabled);
                    _devParams.RspDxParams.BiasTEnable = ToByte(BiasTEnabled);
                    _devParams.RspDxParams.AntennaSel = (SdrPlayApi.RspDx_AntennaSelectT)Math.Clamp(AntennaIndex, 0, 2);
                    _devParams.RspDxParams.RfNotchEnable = ToByte(IsBroadcastNotchEnabled);
                    _devParams.RspDxParams.DabNotchEnable = ToByte(IsDabNotchEnabled);
                    _rxChannelParamsA.RspDxTunerParams.AntennaSel = (SdrPlayApi.RspDx_HdrModeBwT)Math.Clamp(HdrBandwidthIndex, 0, 3);
                    break;
            }

            Marshal.StructureToPtr(_devParams, _deviceParams.PDevParams, fDeleteOld: false);
        }
    }

    private bool IsBroadcastNotchEnabled => NotchFilterMode is 1 or 3;
    private bool IsDabNotchEnabled => NotchFilterMode is 2 or 3;
    private static byte ToByte(bool value) => value ? (byte)1 : (byte)0;

    private float CalculateAdjustedFrequency() => (float)CenterFreqHz * (1f + PpmAdjustment * PpmScale);

    private SdrPlayApi.Bw_MHzT GetBandwidthType(int fsHz)
    {
        if (fsHz >= 8000000) return SdrPlayApi.Bw_MHzT.BW_8_000;
        if (fsHz >= 6000000) return SdrPlayApi.Bw_MHzT.BW_6_000;
        if (fsHz >= 1536000) return SdrPlayApi.Bw_MHzT.BW_1_536;
        return SdrPlayApi.Bw_MHzT.BW_0_200;
    }

    private byte GetDefaultLnaState(byte hwVer) => hwVer switch
    {
        SdrPlayApi.SDRPLAY_RSP1A_ID => 2,
        SdrPlayApi.SDRPLAY_RSPdx_ID or SdrPlayApi.SDRPLAY_RSPdxR2_ID => 0,
        SdrPlayApi.SDRPLAY_RSP2_ID or SdrPlayApi.SDRPLAY_RSPduo_ID => 1,
        _ => 0
    };

    private bool StartStreaming()
    {
        StartSampleDispatcher();
        _cbFns.StreamACbFn = _streamACallback!;
        _cbFns.StreamBCbFn = _streamBCallback!;
        _cbFns.EventCbFn = _eventCallback!;
        
        SdrPlayApi.ErrT err = SdrPlayApi.sdrplay_api_Init(_devices[0].Dev, ref _cbFns, IntPtr.Zero);
        SdrPlayDiagnosticLog.Write(
            "api-init",
            $"result={err} sampleRate={FsHz} frequency={CenterFreqHz} gr={RfGainDb} lna={LnaState}");
        if (err != SdrPlayApi.ErrT.Success)
        {
            StopSampleDispatcher();
            HandleSdrError("SdrPlayApi.sdrplay_api_Init failed", err);
            return false;
        }
        return true;
    }

    private void HandleSdrError(string message, SdrPlayApi.ErrT err, bool notifyUser = true)
    {
        string extraError = "";
        try
        {
            if (_devices[0].Dev != 0)
            {
                nint pErrorInfo = SdrPlayApi.sdrplay_api_GetLastError(ref _devices[0]);
                if (pErrorInfo != IntPtr.Zero)
                {
                    var errorInfo = Marshal.PtrToStructure<SdrPlayApi.sdrplay_api_ErrorInfoT>(pErrorInfo);
                    extraError = $"\n\nAPI Error Detail:\nFile: {errorInfo.file}\nFunc: {errorInfo.function}\nLine: {errorInfo.line}\nMsg: {errorInfo.message}";
                }
            }
        }
        catch (Exception ex)
        {
            extraError = $"\n\n(GetLastError failed: {ex.Message})";
        }

        string text = $"{message}\nError: {err}{extraError}";
        SdrPlayDiagnosticLog.Write(
            "api-error",
            $"message={message.Replace('\r', ' ').Replace('\n', ' ')} result={err} notify={notifyUser} detail={extraError.Replace('\r', ' ').Replace('\n', ' ')}");
        if (!SuppressErrors && notifyUser) WeakReferenceMessenger.Default.Send(new SdrErrorMessage(text));
        Debug.Print(text);
    }
    public void GainChange()
    {
        // SRdeck keeps hardware AGC disabled and applies host-side gain
        // reduction. The SDRplay reference application sends Tuner_Gr only
        // for this operation.
        RequestHardwareUpdate(SdrPlayApi.ReasonForUpdateT.Update_Tuner_Gr);
    }

    public void FreqChange()
    {
        RequestHardwareUpdate(SdrPlayApi.ReasonForUpdateT.Update_Tuner_Frf);
    }

    private void RequestHardwareUpdate(SdrPlayApi.ReasonForUpdateT reason)
    {
        ExecuteUpdate(
            reason == SdrPlayApi.ReasonForUpdateT.Update_Tuner_Frf ? "frequency" : "gain",
            SdrPlayApi.TunerSelectT.Tuner_A,
            reason,
            SdrPlayApi.ReasonForUpdateExtension1T.None,
            SyncTunerParameters);
    }

    public void ApplyLnaAndNotch()
    {
        var reason = SdrPlayApi.ReasonForUpdateT.Update_Tuner_Gr;
        var reasonExt = SdrPlayApi.ReasonForUpdateExtension1T.None;

        byte hwVer = _devices[0].HwVer;
        if (hwVer == SdrPlayApi.SDRPLAY_RSPdx_ID || hwVer == SdrPlayApi.SDRPLAY_RSPdxR2_ID)
        {
            reasonExt |= SdrPlayApi.ReasonForUpdateExtension1T.Update_RspDx_RfNotchControl | 
                         SdrPlayApi.ReasonForUpdateExtension1T.Update_RspDx_RfDabNotchControl;
        }
        else if (hwVer == SdrPlayApi.SDRPLAY_RSP2_ID)
        {
            reason |= SdrPlayApi.ReasonForUpdateT.Update_Rsp2_RfNotchControl;
        }
        else if (hwVer == SdrPlayApi.SDRPLAY_RSPduo_ID)
        {
            reason |= SdrPlayApi.ReasonForUpdateT.Update_RspDuo_Tuner1AmNotchControl |
                      SdrPlayApi.ReasonForUpdateT.Update_RspDuo_RfNotchControl |
                      SdrPlayApi.ReasonForUpdateT.Update_RspDuo_RfDabNotchControl;
        }
        else if (hwVer == SdrPlayApi.SDRPLAY_RSP1A_ID || hwVer == SdrPlayApi.SDRPLAY_RSP1B_ID)
        {
            reason |= SdrPlayApi.ReasonForUpdateT.Update_Rsp1a_RfNotchControl | 
                      SdrPlayApi.ReasonForUpdateT.Update_Rsp1a_RfDabNotchControl;
        }

        ExecuteUpdate(
            "lna-and-notch",
            SdrPlayApi.TunerSelectT.Tuner_A,
            reason,
            reasonExt,
            () =>
            {
                SyncDeviceParametersBlock();
                SyncTunerParameters();
            });
    }

    public void ApplyDeviceSpecificSettings()
    {
        var reason = SdrPlayApi.ReasonForUpdateT.None;
        var reasonExt = SdrPlayApi.ReasonForUpdateExtension1T.None;
        switch (_devices[0].HwVer)
        {
            case SdrPlayApi.SDRPLAY_RSP1A_ID:
            case SdrPlayApi.SDRPLAY_RSP1B_ID:
                reason |= SdrPlayApi.ReasonForUpdateT.Update_Rsp1a_BiasTControl;
                break;

            case SdrPlayApi.SDRPLAY_RSP2_ID:
                reason |= SdrPlayApi.ReasonForUpdateT.Update_Rsp2_BiasTControl |
                          SdrPlayApi.ReasonForUpdateT.Update_Rsp2_AmPortSelect |
                          SdrPlayApi.ReasonForUpdateT.Update_Rsp2_AntennaControl |
                          SdrPlayApi.ReasonForUpdateT.Update_Rsp2_ExtRefControl;
                break;

            case SdrPlayApi.SDRPLAY_RSPduo_ID:
                reason |= SdrPlayApi.ReasonForUpdateT.Update_RspDuo_BiasTControl |
                          SdrPlayApi.ReasonForUpdateT.Update_RspDuo_AmPortSelect |
                          SdrPlayApi.ReasonForUpdateT.Update_RspDuo_ExtRefControl;
                break;

            case SdrPlayApi.SDRPLAY_RSPdx_ID:
            case SdrPlayApi.SDRPLAY_RSPdxR2_ID:
                reasonExt |= SdrPlayApi.ReasonForUpdateExtension1T.Update_RspDx_HdrEnable |
                             SdrPlayApi.ReasonForUpdateExtension1T.Update_RspDx_HdrBw |
                             SdrPlayApi.ReasonForUpdateExtension1T.Update_RspDx_BiasTControl |
                             SdrPlayApi.ReasonForUpdateExtension1T.Update_RspDx_AntennaControl;
                break;
        }

        if (reason == SdrPlayApi.ReasonForUpdateT.None && reasonExt == SdrPlayApi.ReasonForUpdateExtension1T.None)
            return;

        ExecuteUpdate(
            "device-specific-settings",
            SdrPlayApi.TunerSelectT.Tuner_A,
            reason,
            reasonExt,
            () =>
            {
                SyncDeviceParametersBlock();
                SyncTunerParameters();
            });
    }

}
