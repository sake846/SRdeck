using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace SRdeck.SDR;

public static partial class SdrPlayApi
{
    static SdrPlayApi()
    {
        NativeLibrary.SetDllImportResolver(typeof(SdrPlayApi).Assembly, ResolveSdrPlayApi);
    }

    private static IntPtr ResolveSdrPlayApi(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName == "sdrplay_api.dll")
        {
            // Prefer the vendor-installed API. The application-directory fallback
            // supports explicit administrator deployments but must never contain an
            // untrusted DLL. SDRplay's native API is not distributed by SRdeck.
            var paths = new List<string>
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SDRplay", "API", Environment.Is64BitProcess ? "x64" : "x86", "sdrplay_api.dll"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sdrplay_api.dll")
            };

            foreach (var path in paths)
            {
                if (File.Exists(path))
                {
                    if (NativeLibrary.TryLoad(path, out var handle))
                    {
                        return handle;
                    }
                }
            }
        }
        return IntPtr.Zero;
    }

    public const float SDRPLAY_API_VERSION = 3.15f;

    public const int SDRPLAY_MAX_DEVICES = 16;

    public const int SDRPLAY_MAX_TUNERS_PER_DEVICE = 2;

    public const int SDRPLAY_MAX_SER_NO_LEN = 64;

    public const int SDRPLAY_MAX_ROOT_NM_LEN = 32;

    public const int SDRPLAY_RSP1_ID = 1;

    public const int SDRPLAY_RSP1A_ID = 255;

    public const int SDRPLAY_RSP2_ID = 2;

    public const int SDRPLAY_RSPduo_ID = 3;

    public const int SDRPLAY_RSPdx_ID = 4;

    public const int SDRPLAY_RSP1B_ID = 6;

    public const int SDRPLAY_RSPdxR2_ID = 7;

    private const int RSPIA_NUM_LNA_STATES = 10;

    private const int RSPIA_NUM_LNA_STATES_AM = 7;

    private const int RSPIA_NUM_LNA_STATES_LBAND = 9;

    private const int RSPII_NUM_LNA_STATES = 9;

    private const int RSPII_NUM_LNA_STATES_AMPORT = 5;

    private const int RSPII_NUM_LNA_STATES_420MHZ = 6;

    private const int RSPDUO_NUM_LNA_STATES = 10;

    private const int RSPDUO_NUM_LNA_STATES_AMPORT = 5;

    private const int RSPDUO_NUM_LNA_STATES_AM = 7;

    private const int RSPDUO_NUM_LNA_STATES_LBAND = 9;

    private const int RSPDX_NUM_LNA_STATES = 28;

    private const int RSPDX_NUM_LNA_STATES_AMPORT2_0_12 = 19;

    private const int RSPDX_NUM_LNA_STATES_AMPORT2_12_50 = 20;

    private const int RSPDX_NUM_LNA_STATES_AMPORT2_50_60 = 25;

    private const int RSPDX_NUM_LNA_STATES_VHF_BAND3 = 27;

    private const int RSPDX_NUM_LNA_STATES_420MHZ = 21;

    private const int RSPDX_NUM_LNA_STATES_LBAND = 19;

    private const int RSPDX_NUM_LNA_STATES_DX = 22;

    public const int MAX_BB_GR = 59;

}
