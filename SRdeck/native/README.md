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
- `gpufft_process_spectrum` — 1回FFTの表示・ノイズフロア最大値と受信帯域電力をGPUで集約
- `gpufft_process_float` — `float` I/Q入力
- `gpufft_get_last_timings` — native側の処理時間取得
- `gpufft_get_last_readback_timings` — 完了結果の回収、コピー投入、FlushのCPU時間を分離

`gpufft_process_packed`と`gpufft_process_float`は全binのdB値を返します。
メイン画面の1回FFTでは`gpufft_process_spectrum`を使い、FFT点数とHann窓を維持したまま、
画面用の最大値、ノイズフロア用の最大値、受信帯域の4096 binごとの電力和、同調binのdB値だけを返します。
集約境界はCPUで64 bit整数を使って計算し、従来と同じbin範囲をGPUへ渡します。
非同期完了では集約幅と受信帯域の設定も提出タグに対応付けて返し、RSSIへRF校正を一度だけ適用します。
複数回平均と旧DLLは従来の全bin経路を使用します。

GPU FFT本体はStockhamの連続する段を1つのシェーダーで実行します。
測定済みのAMD GPU（Vendor `0x1002` / Device `0x1900`）では次の4回のディスパッチを使います。

| FFT点数 | 統合する段数 | スレッド数/グループ |
|---|---|---|
| 1M | 5 + 5 + 5 + 5 | 32 + 32 + 32 + 32 |
| 2M | 3 + 6 + 6 + 6 | 32 + 256 + 256 + 256 |
| 4M | 4 + 6 + 6 + 6 | 32 + 128 + 128 + 128 |

packed入力では先頭のディスパッチにIQ変換とHann窓処理も統合します。
窓の積を`precise`で保持し、独立した変換シェーダーと同じ丸めを維持します。
CPUのIQ詰め替えはSSE2で8サンプルずつ行い、リング境界と端数を個別に処理します。
他のGPUでは1M〜4M点を6段ずつ処理し、残りを4段・2段・1段で処理します。1M未満は4段統合を使います。
段間の途中結果はレジスタに保持し、中間バッファの読み書きを減らします。
FFT点数、窓、演算の角度とRF校正は維持します。
最適化シェーダーの作成に失敗した場合は6段方式、6段も作成できない場合は4段方式を使います。
[探索方法と測定結果](sr_gpu/tests/fft-tuning.md)に候補と採用理由を記録しています。

比較試験と転送・CPU後処理のベンチマーク:

```powershell
dotnet SRdeck.Tests/bin/Release/net10.0-windows/win-x64/SRdeck.Tests.dll --run-test "GPU spectrum aggregation preserves noise and power" --require-gpu
dotnet SRdeck.Tests/bin/Release/net10.0-windows/win-x64/SRdeck.Tests.dll --benchmark-main-fft
```

比較試験は4K・1M・4M点で、白色ノイズ、弱い信号、帯域外の強い信号、集約幅・同調変更を検査します。
ベンチマークは完了結果の回収、次フレームのコピー投入とFlush、CPU後処理の時間を分離します。
FlushのCPU時間にはドライバーの処理や送信待ちが含まれる場合があり、GPU FFT本体の実行時間とは区別します。
連続投入と100 ms間隔（10回/秒）の両方を測定します。

FFT本体の比較は、次の明示的なCMakeターゲットで実行します。
従来の1段シェーダーと2段・4段・5段・6段統合シェーダーを全11サイズ（4K〜4M）で比較し、
奇数段数と複数バッチ、float入力、表示・ノイズフロア最大値、帯域電力、
強い搬送波と小さいノイズ、最適化シェーダーのフォールバックも検査します。
1M・4Mの速度はGPUタイムスタンプで入力変換・FFT・集約の実行時間を測定し、
CPUのディスパッチ投入時間を別に表示します。GPU計測クエリはテスト実行ファイル内だけで使用します。

```powershell
cmake -S SRdeck/native/sr_gpu -B SRdeck/native/sr_gpu/build -A x64
cmake --build SRdeck/native/sr_gpu/build --config Release --target sr_gpu_fft_tests
SRdeck/native/sr_gpu/build/Release/sr_gpu_fft_tests.exe
SRdeck/native/sr_gpu/build/Release/sr_gpu_fft_tests.exe --checks-only

cmake --build SRdeck/native/sr_gpu/build --config Release --target sr_gpu_fft_tune
SRdeck/native/sr_gpu/build/Release/sr_gpu_fft_tune.exe
SRdeck/native/sr_gpu/build/Release/sr_gpu_fft_tune.exe --finalists
SRdeck/native/sr_gpu/build/Release/sr_gpu_fft_tune.exe --confirm
```

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
- CMake 3.21以上（VS 2022）／4.2以上（VS 2026）
- Visual Studio / Build Tools の「C++ によるデスクトップ開発」ワークロード
  - MSVC x64/x86 ビルドツール（VS 2022: v143、VS 2026: v145）
  - Windows 10 SDK または Windows 11 SDK
  - Windows 用 C++ CMake ツール（別途 CMake をインストールする場合は省略可能）

`CMakeLists.txt` の最小バージョン3.20とは別に、使用する Visual Studio ジェネレーターの
対応バージョンが必要です。インストールとエラー別の対処は
[開発環境](../../docs/wiki/Development-Environment.md) を参照してください。
以下の `-A x64` を使うコマンドには Visual Studio ジェネレーターが必要です。
`CMAKE_GENERATOR` が Ninja / NMake になっている場合は、PowerShell で
`$env:CMAKE_GENERATOR = 'Visual Studio 17 2022'`（VS 2026 は `Visual Studio 18 2026`）を指定してください。
Developer PowerShell で実行する場合も、先に MSVC と Windows SDK のインストールを確認してください。

### native DLLだけをビルドする

```powershell
cmake -S .\SRdeck\native\sr_fft -B .\SRdeck\native\sr_fft\build -A x64
cmake --build .\SRdeck\native\sr_fft\build --config Release

cmake -S .\SRdeck\native\sr_gpu -B .\SRdeck\native\sr_gpu\build -A x64
cmake --build .\SRdeck\native\sr_gpu\build --config Release
```

既存のbuildフォルダーを別のVisual Studioやジェネレーターで作っている場合は、
そのbuildフォルダーを別名へ退避してから再実行してください。

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
