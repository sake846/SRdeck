using System.Runtime.InteropServices;

namespace SRdeck.SDR;

// Managed declarations for Great Scott Gadgets' libhackrf API. The native DLL
// is supplied separately by the user and is not distributed with SRdeck.
internal static class HackRfApi
{
    private const string DllName = "hackrf.dll";
    public const int Success = 0;
    public const int Streaming = 1;

    [StructLayout(LayoutKind.Sequential)]
    internal struct HackRfTransfer
    {
        public IntPtr Device;
        public IntPtr Buffer;
        public int BufferLength;
        public int ValidLength;
        public IntPtr RxContext;
        public IntPtr TxContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ReadPartIdSerialNumber
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
        public uint[] PartId;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public uint[] SerialNumber;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int SampleBlockCallback(IntPtr transfer);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int hackrf_init();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int hackrf_exit();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int hackrf_open(out IntPtr device);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int hackrf_close(IntPtr device);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int hackrf_start_rx(IntPtr device, SampleBlockCallback callback, IntPtr rxContext);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int hackrf_stop_rx(IntPtr device);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int hackrf_is_streaming(IntPtr device);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int hackrf_set_freq(IntPtr device, ulong frequencyHz);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int hackrf_set_sample_rate_manual(IntPtr device, uint frequencyHz, uint divider);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern uint hackrf_compute_baseband_filter_bw_round_down_lt(uint bandwidthHz);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int hackrf_set_baseband_filter_bandwidth(IntPtr device, uint bandwidthHz);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int hackrf_set_lna_gain(IntPtr device, uint gainDb);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int hackrf_set_vga_gain(IntPtr device, uint gainDb);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int hackrf_set_amp_enable(IntPtr device, byte enabled);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int hackrf_board_id_read(IntPtr device, out byte boardId);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr hackrf_board_id_name(int boardId);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int hackrf_board_partid_serialno_read(IntPtr device, ref ReadPartIdSerialNumber serialNumber);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr hackrf_error_name(int errorCode);

    internal static string GetErrorName(int errorCode)
    {
        try
        {
            return Marshal.PtrToStringAnsi(hackrf_error_name(errorCode)) ?? errorCode.ToString();
        }
        catch
        {
            return errorCode.ToString();
        }
    }
}
