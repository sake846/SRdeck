# native

`native`には、SRdeck本体から`DllImport`で呼び出すCMakeプロジェクトを置いています。
このリポジトリに含まれるnativeターゲットは次の2つです。

| ターゲット | DLL | 用途 |
| --- | --- | --- |
| `sr_fft` | `sr_fft.dll` | CPU FFT |
| `sr_gpu` | `sr_gpu.dll` | GPU FFT、GPUチャネル変換、WPF描画 |

native DLLは、ビルド時に`SRdeck.exe`と同じ出力フォルダーへコピーされます。

## sr_fft

ソース:

- [`CMakeLists.txt`](sr_fft/CMakeLists.txt)
- [`cpufft.cpp`](sr_fft/cpufft.cpp)

C#側では[`SRdeckCore.SignalProcessing/FastFourierTransform.cs`](../../SRdeckCore.SignalProcessing/FastFourierTransform.cs)から呼び出します。
公開しているC APIは次の2つです。

```c
int cpufft_execute_db(
    Complex32* samples,
    int sampleSize,
    int logN,
    float bias,
    float* outputDb);

int cpufft_execute_power(
    Complex32* samples,
    int sampleSize,
    int logN,
    float* outputPower);
```

`sampleSize`は`2^logN`である必要があります。FFT計画はFFTサイズごとにキャッシュされ、
CPUの機能検出結果に応じてAVX-512、AVX2、スカラーの経路を選択します。

## sr_gpu

ソース:

- [`CMakeLists.txt`](sr_gpu/CMakeLists.txt)
- [`gpufft.cpp`](sr_gpu/gpufft.cpp)
- [`gpufft_channel.cpp`](sr_gpu/gpufft_channel.cpp)
- [`gpufft_draw.cpp`](sr_gpu/gpufft_draw.cpp)
- [`gpufft_common.h`](sr_gpu/gpufft_common.h)
- [`gpufft_shaders.h`](sr_gpu/gpufft_shaders.h)

Direct3D 11、DirectCompute、DXGIを使用します。C#側の呼び出し元は次のとおりです。

- [`GpuFftRunner.cs`](../../SRdeck/DSP/GpuFftRunner.cs) — GPU FFT
- [`NativeStandardChannelGpuBackend.cs`](../../SRdeck/Services/Plugins/NativeStandardChannelGpuBackend.cs) — GPUチャネル変換
- [`NativeGpuDrawApi.cs`](../../SRdeck/Renderers/NativeGpuDrawApi.cs) — WPF向けGPU描画

### GPU FFT API

- `gpufft_create` / `gpufft_destroy`
- `gpufft_process_packed` — `short` I/Q入力
- `gpufft_process_float` — `float` I/Q入力
- `gpufft_get_last_timings` — native側の処理時間取得

FFTの結果は、バッチごとのスペクトラムを連結した`float`配列として返します。

### GPUチャネル変換API

- `gpuchannel_is_available`
- `gpuchannel_get_adapter_identity`
- `gpuchannel_create` / `gpuchannel_destroy`
- `gpuchannel_reset`
- `gpuchannel_get_output_capacity`
- `gpuchannel_submit` / `gpuchannel_collect`
- `gpuchannel_process` — submitとcollectを一度に行う同期API
- `gpuchannel_get_last_timings`

### GPU描画API

- `gpudraw_shutdown`
- `gpudraw_create_surface` / `gpudraw_destroy_surface`
- `gpudraw_clear_surface`
- `gpudraw_upload_bgra_surface`
- `gpudraw_scroll_upload_top_row`
- `gpudraw_scroll_upload_row_region`
- `gpudraw_draw_lines` / `gpudraw_draw_triangles`
- `gpudraw_draw_lines_ex` / `gpudraw_draw_triangles_ex`

## ビルド出力

CMakeの中間生成物は次に作られます。

```text
SRdeck/native/sr_fft/build/<Configuration>/sr_fft.dll
SRdeck/native/sr_gpu/build/<Configuration>/sr_gpu.dll
```

`SRdeck/SRdeck.csproj`のビルド後処理によって、最終的に`SRdeck.exe`と同じ出力フォルダーへ
次の2つがコピーされます。

```text
sr_fft.dll
sr_gpu.dll
```

`dotnet publish`を使う配布パッケージでは、発行先フォルダーにも同じ2つのDLLがコピーされ、
そのままZIPに含まれます。

出力フォルダーは、構成やRIDにより次のように変わります。

```text
SRdeck/bin/Release/net10.0-windows/
SRdeck/bin/Release/net10.0-windows/win-x64/
```

## ビルド方法

リポジトリのルートで実行します。

### 前提

- Windows x64
- .NET 10 SDK
- CMake
- Visual StudioのC++ビルドツールとWindows SDK

CMakeがC++コンパイラーを見つけられない場合は、Visual StudioのDeveloper PowerShellまたは
`x64 Native Tools Command Prompt for VS`から実行してください。

### native DLLだけをビルドする

```powershell
cmake -S .\SRdeck\native\sr_fft -B .\SRdeck\native\sr_fft\build -A x64
cmake --build .\SRdeck\native\sr_fft\build --config Release

cmake -S .\SRdeck\native\sr_gpu -B .\SRdeck\native\sr_gpu\build -A x64
cmake --build .\SRdeck\native\sr_gpu\build --config Release
```

既存のbuildフォルダーを別のVisual Studioやジェネレーターで作っている場合は、
そのbuildフォルダーを削除してから再実行してください。

### SRdeckとnative DLLをまとめてビルドする

通常はこちらを使用します。

```powershell
dotnet restore .\SRdeck.sln
dotnet build .\SRdeck.sln -c Release
```

`SRdeck/SRdeck.csproj`はWindowsでのビルド前に、次の順序でnativeを自動ビルドします。

1. `sr_fft`
2. `sr_gpu`

CMakeの実行ファイルは次の順に決まります。

1. MSBuildプロパティ`NativeCMakeExe`または`CMAKE_EXE`環境変数で指定した値
2. 対応するVisual Studio同梱のCMake
3. PATH上の`cmake`

CMakeを利用できない場合は、native DLLを作れないためビルドは失敗します。

### CMakeの場所を明示する

```powershell
$env:CMAKE_EXE = 'C:\Program Files\CMake\bin\cmake.exe'
dotnet build .\SRdeck\SRdeck.csproj -c Release
```

## 実行時の注意

- `sr_fft.dll`と`sr_gpu.dll`は`SRdeck.exe`と同じフォルダーに配置してください。
- `sr_gpu.dll`のGPU FFTとGPUチャネル変換には、Direct3D 11対応GPUと正常なGPUドライバーが必要です。
- GPU FFTの初期化に失敗する環境では、SRdeckの設定で「FFT GPUの使用」を`Off (CPU)`にしてください。
