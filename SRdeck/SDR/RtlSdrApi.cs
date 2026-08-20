using System;
using System.Runtime.InteropServices;

namespace SRdeck.SDR;

// These managed declarations correspond to Osmocom rtl-sdr's GPL-2.0-or-later
// public API. See THIRD-PARTY-NOTICES.md. The native DLL is not distributed.
internal static class RtlSdrApi
{
    private const string DllName = "rtlsdr.dll";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void RtlSdrReadAsyncCbT(IntPtr buf, uint len, IntPtr ctx);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint rtlsdr_get_device_count();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr rtlsdr_get_device_name(uint index);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rtlsdr_get_device_usb_strings(uint index, IntPtr manufact, IntPtr product, IntPtr serial);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rtlsdr_open(ref IntPtr dev, uint index);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rtlsdr_close(IntPtr dev);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rtlsdr_set_center_freq(IntPtr dev, uint freq);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rtlsdr_set_sample_rate(IntPtr dev, uint rate);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rtlsdr_set_freq_correction(IntPtr dev, int ppm);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rtlsdr_get_tuner_gains(IntPtr dev, IntPtr gains);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rtlsdr_set_tuner_gain_mode(IntPtr dev, int manual);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rtlsdr_set_tuner_gain(IntPtr dev, int gain);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rtlsdr_set_agc_mode(IntPtr dev, int on);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rtlsdr_get_tuner_gain(IntPtr dev);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rtlsdr_get_tuner_type(IntPtr dev);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rtlsdr_set_direct_sampling(IntPtr dev, int on);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rtlsdr_set_offset_tuning(IntPtr dev, int on);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rtlsdr_reset_buffer(IntPtr dev);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rtlsdr_read_async(IntPtr dev, RtlSdrReadAsyncCbT cb, IntPtr ctx, uint bufNum, uint bufLen);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rtlsdr_cancel_async(IntPtr dev);
}
