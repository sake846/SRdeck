using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace SRdeck.Renderers;

internal sealed class D3DImageInterop : IDisposable
{
    private const uint D3D_SDK_VERSION = 32;
    private const uint D3DADAPTER_DEFAULT = 0;
    private const uint D3DDEVTYPE_HAL = 1;
    private const uint D3DCREATE_HARDWARE_VERTEXPROCESSING = 0x00000040;
    private const uint D3DCREATE_MULTITHREADED = 0x00000004;
    private const uint D3DCREATE_FPU_PRESERVE = 0x00000002;
    private const int D3DFMT_A8R8G8B8 = 21;
    private const uint D3DUSAGE_RENDERTARGET = 0x00000001;
    private const uint D3DPOOL_DEFAULT = 0;
    private const uint D3DMULTISAMPLE_NONE = 0;
    private const uint D3DSWAPEFFECT_DISCARD = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DPRESENT_PARAMETERS
    {
        public uint BackBufferWidth;
        public uint BackBufferHeight;
        public int BackBufferFormat;
        public uint BackBufferCount;
        public uint MultiSampleType;
        public uint MultiSampleQuality;
        public uint SwapEffect;
        public IntPtr hDeviceWindow;
        [MarshalAs(UnmanagedType.Bool)] public bool Windowed;
        [MarshalAs(UnmanagedType.Bool)] public bool EnableAutoDepthStencil;
        public int AutoDepthStencilFormat;
        public uint Flags;
        public uint FullScreen_RefreshRateInHz;
        public uint PresentationInterval;
    }

    [DllImport("d3d9.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int Direct3DCreate9Ex(uint sdkVersion, out IntPtr d3d9ex);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("02177241-69FC-400C-8FF1-93A44DF6861D")]
    private interface IDirect3D9Ex
    {
        int RegisterSoftwareDevice(IntPtr pInitializeFunction);
        uint GetAdapterCount();
        int GetAdapterIdentifier(uint Adapter, uint Flags, IntPtr pIdentifier);
        uint GetAdapterModeCount(uint Adapter, int Format);
        int EnumAdapterModes(uint Adapter, int Format, uint Mode, IntPtr pMode);
        int GetAdapterDisplayMode(uint Adapter, IntPtr pMode);
        int CheckDeviceType(uint Adapter, uint DevType, int DisplayFormat, int BackBufferFormat, [MarshalAs(UnmanagedType.Bool)] bool bWindowed);
        int CheckDeviceFormat(uint Adapter, uint DeviceType, int AdapterFormat, uint Usage, uint RType, int CheckFormat);
        int CheckDeviceMultiSampleType(uint Adapter, uint DeviceType, int SurfaceFormat, [MarshalAs(UnmanagedType.Bool)] bool Windowed, uint MultiSampleType, out uint pQualityLevels);
        int CheckDepthStencilMatch(uint Adapter, uint DeviceType, int AdapterFormat, int RenderTargetFormat, int DepthStencilFormat);
        int CheckDeviceFormatConversion(uint Adapter, uint DeviceType, int SourceFormat, int TargetFormat);
        int GetDeviceCaps(uint Adapter, uint DeviceType, IntPtr pCaps);
        IntPtr GetAdapterMonitor(uint Adapter);
        int CreateDevice(uint Adapter, uint DeviceType, IntPtr hFocusWindow, uint BehaviorFlags, ref D3DPRESENT_PARAMETERS pPresentationParameters, out IntPtr ppReturnedDeviceInterface);
        int GetAdapterModeCountEx(uint Adapter, IntPtr pFilter);
        int EnumAdapterModesEx(uint Adapter, IntPtr pFilter, uint Mode, IntPtr pMode);
        int GetAdapterDisplayModeEx(uint Adapter, IntPtr pMode, IntPtr pRotation);
        int CreateDeviceEx(uint Adapter, uint DeviceType, IntPtr hFocusWindow, uint BehaviorFlags, ref D3DPRESENT_PARAMETERS pPresentationParameters, IntPtr pFullscreenDisplayMode, out IntPtr ppReturnedDeviceInterface);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("B18B10CE-2649-405A-870F-95F777D4313A")]
    private interface IDirect3DDevice9Ex
    {
        int TestCooperativeLevel();
        uint GetAvailableTextureMem();
        int EvictManagedResources();
        int GetDirect3D(out IntPtr ppD3D9);
        int GetDeviceCaps(IntPtr pCaps);
        int GetDisplayMode(uint iSwapChain, IntPtr pMode);
        int GetCreationParameters(IntPtr pParameters);
        int SetCursorProperties(uint XHotSpot, uint YHotSpot, IntPtr pCursorBitmap);
        void SetCursorPosition(int X, int Y, uint Flags);
        [return: MarshalAs(UnmanagedType.Bool)] bool ShowCursor([MarshalAs(UnmanagedType.Bool)] bool bShow);
        int CreateAdditionalSwapChain(ref D3DPRESENT_PARAMETERS pPresentationParameters, out IntPtr pSwapChain);
        int GetSwapChain(uint iSwapChain, out IntPtr pSwapChain);
        uint GetNumberOfSwapChains();
        int Reset(ref D3DPRESENT_PARAMETERS pPresentationParameters);
        int Present(IntPtr pSourceRect, IntPtr pDestRect, IntPtr hDestWindowOverride, IntPtr pDirtyRegion);
        int GetBackBuffer(uint iSwapChain, uint iBackBuffer, uint Type, out IntPtr ppBackBuffer);
        int GetRasterStatus(uint iSwapChain, IntPtr pRasterStatus);
        int SetDialogBoxMode([MarshalAs(UnmanagedType.Bool)] bool bEnableDialogs);
        void SetGammaRamp(uint iSwapChain, uint Flags, IntPtr pRamp);
        void GetGammaRamp(uint iSwapChain, IntPtr pRamp);
        int CreateTexture(uint Width, uint Height, uint Levels, uint Usage, int Format, uint Pool, out IntPtr ppTexture, ref IntPtr pSharedHandle);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("85C31227-3DE5-4F00-9B3A-F11AC38C18B5")]
    private interface IDirect3DTexture9
    {
        int GetDevice(out IntPtr ppDevice);
        int SetPrivateData(ref Guid refguid, IntPtr pData, uint SizeOfData, uint Flags);
        int GetPrivateData(ref Guid refguid, IntPtr pData, ref uint pSizeOfData);
        int FreePrivateData(ref Guid refguid);
        uint SetPriority(uint PriorityNew);
        uint GetPriority();
        void PreLoad();
        uint GetType();
        int SetLOD(uint LODNew);
        uint GetLOD();
        uint GetLevelCount();
        int SetAutoGenFilterType(uint FilterType);
        uint GetAutoGenFilterType();
        void GenerateMipSubLevels();
        int GetLevelDesc(uint Level, IntPtr pDesc);
        int GetSurfaceLevel(uint Level, out IntPtr ppSurfaceLevel);
    }

    public D3DImage Image { get; } = new();
    public double LastLockMilliseconds { get; private set; }
    public double LastUnlockMilliseconds { get; private set; }
    private static readonly object SharedDeviceSync = new();
    private static IDirect3D9Ex? s_sharedD3d9;
    private static IDirect3DDevice9Ex? s_sharedDevice;
    private static int s_sharedDeviceUsers;

    private IDirect3DTexture9? _texture;
    private IntPtr _surface = IntPtr.Zero;
    private int _width = 1;
    private int _height = 1;
    private bool _updateLocked;
    private int _updateGeneration;
    private bool _hasSharedDeviceLease;
    private bool _isBackBufferInvalid;

    public bool IsReady => _surface != IntPtr.Zero && Image.IsFrontBufferAvailable && !_isBackBufferInvalid;

    public D3DImageInterop()
    {
        Image.IsFrontBufferAvailableChanged += OnIsFrontBufferAvailableChanged;
    }

    private void OnIsFrontBufferAvailableChanged(object? sender, DependencyPropertyChangedEventArgs e)
    {
        if (Image.IsFrontBufferAvailable)
        {
            try
            {
                if (_surface != IntPtr.Zero)
                {
                    Image.Lock();
                    Image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, _surface);
                    Image.AddDirtyRect(new Int32Rect(0, 0, _width, _height));
                    Image.Unlock();
                    _isBackBufferInvalid = false;
                }
            }
            catch
            {
                _isBackBufferInvalid = true;
            }
        }
        else
        {
            _isBackBufferInvalid = true;
        }
    }

    public bool TryInitializeFromSharedHandle(IntPtr sharedHandle, int width, int height)
    {
        try
        {
            _width = Math.Max(1, width);
            _height = Math.Max(1, height);
            if (!TryAcquireSharedDevice(out var device)) return false;
            _hasSharedDeviceLease = true;

            IntPtr sharedHandleLocal = sharedHandle;
            int hr = device.CreateTexture((uint)_width, (uint)_height, 1, D3DUSAGE_RENDERTARGET, D3DFMT_A8R8G8B8, D3DPOOL_DEFAULT, out var texPtr, ref sharedHandleLocal);
            if (hr != 0 || texPtr == IntPtr.Zero) return false;
            _texture = (IDirect3DTexture9)Marshal.GetObjectForIUnknown(texPtr);
            hr = _texture.GetSurfaceLevel(0, out _surface);
            if (hr != 0 || _surface == IntPtr.Zero) return false;

            Image.Lock();
            Image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, _surface);
            Image.AddDirtyRect(new Int32Rect(0, 0, _width, _height));
            Image.Unlock();
            _isBackBufferInvalid = false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool TryBeginUpdate(out UpdateScope scope)
    {
        scope = default;
        if (_surface == IntPtr.Zero || _updateLocked || !Image.IsFrontBufferAvailable || _isBackBufferInvalid) return false;

        // This is called from CompositionTarget.Rendering. A zero-duration TryLock can
        // repeatedly lose to WPF's render thread and starve the surface indefinitely.
        // Lock before touching the shared texture so WPF and D3D11 remain synchronized,
        // and wait for the previous front-buffer copy to finish when necessary.
        long lockStarted = Stopwatch.GetTimestamp();
        Image.Lock();
        LastLockMilliseconds = Stopwatch.GetElapsedTime(lockStarted).TotalMilliseconds;

        _updateLocked = true;
        int generation = ++_updateGeneration;
        scope = new UpdateScope(this, generation);
        return true;
    }

    private void CompleteUpdate(int generation)
    {
        if (!_updateLocked || generation != _updateGeneration) return;
        long unlockStarted = Stopwatch.GetTimestamp();
        try
        {
            Image.AddDirtyRect(new Int32Rect(0, 0, _width, _height));
        }
        finally
        {
            _updateLocked = false;
            Image.Unlock();
            LastUnlockMilliseconds = Stopwatch.GetElapsedTime(unlockStarted).TotalMilliseconds;
        }
    }

    public void Dispose()
    {
        try
        {
            Image.IsFrontBufferAvailableChanged -= OnIsFrontBufferAvailableChanged;
            if (_updateLocked)
            {
                _updateLocked = false;
                Image.Unlock();
            }
            Image.Lock();
            Image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, IntPtr.Zero);
            Image.Unlock();
        }
        catch { }

        if (_surface != IntPtr.Zero)
        {
            Marshal.Release(_surface);
            _surface = IntPtr.Zero;
        }
        if (_texture != null) Marshal.ReleaseComObject(_texture);
        _texture = null;
        if (_hasSharedDeviceLease)
        {
            _hasSharedDeviceLease = false;
            ReleaseSharedDevice();
        }
    }

    private static bool TryAcquireSharedDevice(out IDirect3DDevice9Ex device)
    {
        lock (SharedDeviceSync)
        {
            if (s_sharedDevice != null)
            {
                try
                {
                    int testHr = s_sharedDevice.TestCooperativeLevel();
                    if (testHr == 0)
                    {
                        s_sharedDeviceUsers++;
                        device = s_sharedDevice;
                        return true;
                    }
                }
                catch { }

                ForceReleaseSharedDeviceInternal();
            }

            IntPtr d3dPtr = IntPtr.Zero;
            IntPtr devicePtr = IntPtr.Zero;
            IDirect3D9Ex? newD3d9 = null;
            IDirect3DDevice9Ex? newDevice = null;
            try
            {
                if (Direct3DCreate9Ex(D3D_SDK_VERSION, out d3dPtr) != 0 || d3dPtr == IntPtr.Zero)
                {
                    device = null!;
                    return false;
                }

                newD3d9 = (IDirect3D9Ex)Marshal.GetObjectForIUnknown(d3dPtr);
                var presentParameters = new D3DPRESENT_PARAMETERS
                {
                    Windowed = true,
                    SwapEffect = D3DSWAPEFFECT_DISCARD,
                    hDeviceWindow = GetDesktopWindow(),
                    PresentationInterval = 0
                };

                int hResult = newD3d9.CreateDeviceEx(
                    D3DADAPTER_DEFAULT,
                    D3DDEVTYPE_HAL,
                    IntPtr.Zero,
                    D3DCREATE_HARDWARE_VERTEXPROCESSING | D3DCREATE_MULTITHREADED | D3DCREATE_FPU_PRESERVE,
                    ref presentParameters,
                    IntPtr.Zero,
                    out devicePtr);
                if (hResult != 0 || devicePtr == IntPtr.Zero)
                {
                    Marshal.ReleaseComObject(newD3d9);
                    newD3d9 = null;
                    device = null!;
                    return false;
                }

                newDevice = (IDirect3DDevice9Ex)Marshal.GetObjectForIUnknown(devicePtr);
                s_sharedD3d9 = newD3d9;
                s_sharedDevice = newDevice;
                s_sharedDeviceUsers = 1;
                device = s_sharedDevice;
                return true;
            }
            catch
            {
                if (newDevice != null) Marshal.ReleaseComObject(newDevice);
                if (newD3d9 != null) Marshal.ReleaseComObject(newD3d9);
                device = null!;
                return false;
            }
            finally
            {
                if (devicePtr != IntPtr.Zero) Marshal.Release(devicePtr);
                if (d3dPtr != IntPtr.Zero) Marshal.Release(d3dPtr);
            }
        }
    }

    private static void ReleaseSharedDevice()
    {
        lock (SharedDeviceSync)
        {
            if (s_sharedDeviceUsers > 0) s_sharedDeviceUsers--;
            if (s_sharedDeviceUsers != 0) return;

            ForceReleaseSharedDeviceInternal();
        }
    }

    public static void ForceReleaseSharedDevice()
    {
        lock (SharedDeviceSync)
        {
            ForceReleaseSharedDeviceInternal();
        }
    }

    private static void ForceReleaseSharedDeviceInternal()
    {
        s_sharedDeviceUsers = 0;
        if (s_sharedDevice != null)
        {
            try { Marshal.ReleaseComObject(s_sharedDevice); } catch { }
            s_sharedDevice = null;
        }
        if (s_sharedD3d9 != null)
        {
            try { Marshal.ReleaseComObject(s_sharedD3d9); } catch { }
            s_sharedD3d9 = null;
        }
    }

    public readonly struct UpdateScope : IDisposable
    {
        private readonly D3DImageInterop? _owner;
        private readonly int _generation;

        internal UpdateScope(D3DImageInterop owner, int generation)
        {
            _owner = owner;
            _generation = generation;
        }

        public void Dispose() => _owner?.CompleteUpdate(_generation);
    }
}
