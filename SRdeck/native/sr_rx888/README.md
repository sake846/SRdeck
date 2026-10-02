# sr_rx888

`sr_rx888` is a separate native project intended to replace the external
`sddc.dll` dependency used for RX-888 MK2 support with a native backend.

Current state:

- exports the ABI subset used by `SRdeck` via `sr_rx888.dll`
- keeps handle state and parameter validation on the host side
- separates the public ABI from the transport/hardware backend
- dynamically loads `libusb-1.0.dll` and enumerates RX-888 MK2 FX3 devices
- opens the FX3 streamer device and verifies `TESTFX3`
- sends EP0 vendor requests for `STARTADC`, `TUNERINIT`, `TUNERTUNE`, `TUNERSTDBY`, `GPIOFX3`, `SETARGFX3`, `STARTFX3`, and `STOPFX3`
- maps `SRdeck` gain and bias controls onto RX-888 MK2 `SETARGFX3` and GPIO state
- reads bulk samples from endpoint `0x81` in `rx888_handle_events`
- converts raw `int16` ADC samples into `float32` IQ with a native host-side DDC
- uses a native, AVX2-optimized FFT-based overlap-save decimator
- downloads `SDDC_FX3.img` when the device enumerates in FX3 bootloader mode
- can open an already-running streamer device even when `SDDC_FX3.img` is not present

The intended output file name is `sr_rx888.dll`.

## Build

```powershell
$bat = 'C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\Tools\VsDevCmd.bat'
$cl = 'C:/Program Files/Microsoft Visual Studio/2022/Community/VC/Tools/MSVC/14.44.35207/bin/Hostx64/x64/cl.exe'
cmd /c "call `"$bat`" -arch=x64 -host_arch=x64 && cmake -S .\SRdeck\native\sr_rx888 -B .\SRdeck\native\sr_rx888\build -G Ninja -DCMAKE_CXX_COMPILER=`"$cl`" && cmake --build .\SRdeck\native\sr_rx888\build"
```

Generated file:

- `SRdeck\native\sr_rx888\build\sr_rx888.dll`

Runtime requirement:

- place `libusb-1.0.dll` next to the built `sr_rx888.dll` or next to the final application executable

## Design Notes

- Public ABI is declared in `include/sr_rx888.h`.
- Internal state and backend interface live in `src/sr_rx888.cpp`.
- Hardware-specific work should be added behind a backend interface instead of
  inside exported functions directly.

## Remaining Work

1. Tune transfer sizing and timeout behavior for stable sustained streaming.
2. Continue performance tuning and behavior validation against upstream `r2iq`.
