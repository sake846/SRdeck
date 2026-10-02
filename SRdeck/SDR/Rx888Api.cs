using System;
using System.Runtime.InteropServices;

namespace SRdeck.SDR;

internal static class Rx888Api
{
    private const string DllName = "sr_rx888.dll";

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rx888DeviceInfo
    {
        public IntPtr Manufacturer;
        public IntPtr Product;
        public IntPtr SerialNumber;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void Rx888ReadAsyncCb(uint dataSize, IntPtr data, IntPtr context);

    internal enum RfMode
    {
        NoRfMode = 0,
        HfMode = 1,
        VhfMode = 2
    }

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "rx888_open")]
    internal static extern IntPtr Open(int index, [MarshalAs(UnmanagedType.LPStr)] string imageFile);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "rx888_get_device_info")]
    internal static extern int GetDeviceInfo(out IntPtr deviceInfos);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "rx888_free_device_info")]
    internal static extern int FreeDeviceInfo(IntPtr deviceInfos);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "rx888_close")]
    internal static extern void Close(IntPtr handle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "rx888_get_backend_name")]
    internal static extern IntPtr GetBackendName(IntPtr handle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "rx888_set_sample_rate")]
    internal static extern int SetSampleRate(IntPtr handle, double sampleRate);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "rx888_set_adc_frequency")]
    internal static extern int SetAdcFrequency(IntPtr handle, double adcFrequency);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "rx888_set_rf_mode")]
    internal static extern int SetRfMode(IntPtr handle, RfMode rfMode);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "rx888_set_tuner_frequency")]
    internal static extern int SetTunerFrequency(IntPtr handle, double frequency);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "rx888_set_tuner_rf_attenuation")]
    internal static extern int SetTunerRfAttenuation(IntPtr handle, double attenuation);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "rx888_set_tuner_if_attenuation")]
    internal static extern int SetTunerIfAttenuation(IntPtr handle, double attenuation);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "rx888_set_adc_dither")]
    internal static extern int SetAdcDither(IntPtr handle, int dither);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "rx888_set_adc_random")]
    internal static extern int SetAdcRandom(IntPtr handle, int random);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "rx888_set_vhf_bias")]
    internal static extern int SetVhfBias(IntPtr handle, int bias);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "rx888_set_hf_bias")]
    internal static extern int SetHfBias(IntPtr handle, int bias);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "rx888_set_hf_attenuation")]
    internal static extern int SetHfAttenuation(IntPtr handle, double attenuation);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "rx888_set_async_params")]
    internal static extern int SetAsyncParams(
        IntPtr handle,
        uint frameSize,
        uint numFrames,
        Rx888ReadAsyncCb callback,
        IntPtr callbackContext);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "rx888_start_streaming")]
    internal static extern int StartStreaming(IntPtr handle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "rx888_handle_events")]
    internal static extern int HandleEvents(IntPtr handle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "rx888_stop_streaming")]
    internal static extern int StopStreaming(IntPtr handle);
}
