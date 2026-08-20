using System.Runtime.InteropServices;

namespace SRdeck.SDR;

public static partial class SdrPlayApi
{
    [DllImport("sdrplay_api.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern ErrT sdrplay_api_Open();

    [DllImport("sdrplay_api.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern ErrT sdrplay_api_Close();

    [DllImport("sdrplay_api.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern ErrT sdrplay_api_ApiVersion(ref float apiVer);

    [DllImport("sdrplay_api.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern ErrT sdrplay_api_LockDeviceApi();

    [DllImport("sdrplay_api.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern ErrT sdrplay_api_UnlockDeviceApi();

    [DllImport("sdrplay_api.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern ErrT sdrplay_api_GetDevices(ref DeviceT devices, ref uint numdevs, uint maxDevs);

    [DllImport("sdrplay_api.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern ErrT sdrplay_api_SelectDevice(ref DeviceT device);

    [DllImport("sdrplay_api.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern ErrT sdrplay_api_ReleaseDevice(ref DeviceT device);

    [DllImport("sdrplay_api.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern nint sdrplay_api_GetLastError(ref DeviceT device);

    [DllImport("sdrplay_api.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern ErrT sdrplay_api_GetLastErrorByType(ref DeviceT device, int type, ref ulong time);

    [DllImport("sdrplay_api.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern ErrT sdrplay_api_DisableHeartbeat();

    [DllImport("sdrplay_api.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern ErrT sdrplay_api_DebugEnable(int dev, DbgLvlT enable);

    [DllImport("sdrplay_api.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern ErrT sdrplay_api_GetDeviceParams(nint dev, ref nint deviceParams);

    [DllImport("sdrplay_api.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern ErrT sdrplay_api_Init(nint dev, ref CallbackFnsT callbackFns, nint cbContext);

    [DllImport("sdrplay_api.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern ErrT sdrplay_api_Uninit(nint dev);

    [DllImport("sdrplay_api.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern ErrT sdrplay_api_Update(nint dev, TunerSelectT tuner, ReasonForUpdateT reasonForUpdate, ReasonForUpdateExtension1T reasonForUpdateExt1);

    [DllImport("sdrplay_api.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern ErrT sdrplay_api_SwapRspDuoActiveTuner(nint dev, nint currentTuner, RspDuo_AmPortSelectT tuner1AmPortSel);

    [DllImport("sdrplay_api.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern ErrT sdrplay_api_SwapRspDuoDualTunerModeSampleRate(nint dev, nint currentSampleRate);
}