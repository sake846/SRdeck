# システム要件

## 実行環境

- Windows x64
- x64対応CPU
- SDR処理と画面表示に十分なメモリ
- 対応SDRと、そのWindows用ドライバー／API

公式ZIPは`.NET 10`ベースのフレームワーク依存`win-x64`パッケージです。実行には.NET 10 Desktop Runtime (x64)、ソースからのビルドには.NET 10 SDKが必要です。

地図などの埋め込みWeb表示を使用する機能にはMicrosoft Edge WebView2 Runtimeが必要です。地図を開くとLeafletとOpenStreetMapタイルの提供元へHTTPS接続し、IPアドレスと表示地域に対応するタイル座標が送信されます。

## 対応しているSDR入力

| 入力 | 必要なもの |
|---|---|
| SDRplay | SDRplay API 3.x。ホストはAPI 3.15を対象にしています |
| RTL-SDR | 対応する`rtlsdr.dll`と、デバイスに適したWinUSBドライバー |

デバイス種別は`Auto`、`SDRplay`、`RTL-SDR`から選択できます。`Auto`ではSDRplayを先に、続いてRTL-SDRを検出します。

HackRF、Airspy、一般的なRaw IQファイルは、現在のユーザー向け入力として実装されていません。

## 推奨構成

- 複数MHzのRATEや多数チャネルを処理する場合は、余裕のあるマルチコアCPU
- 高解像度FFTを使う場合は、対応GPUと最新ドライバー
- SDR本体、USB帯域、PC性能に合ったRATE設定

GPU処理を無効にしてCPUで動作させることもできます。処理落ちや画面更新の遅れが出る場合は、RATEまたはFFT解像度を下げてください。
