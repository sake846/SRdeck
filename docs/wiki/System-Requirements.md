# システム要件

## 対応環境

| 項目 | 必須／対応内容 |
|---|---|
| OS | 64-bit版Windows |
| CPU | x64対応CPU |
| ランタイム | .NET 10 Desktop Runtime (x64) |
| SDR | SDRplayまたはRTL-SDR |
| USB | 使用するSDRとRATEを安定して転送できるポート／コントローラー |
| 表示 | WPFを正常に描画できるグラフィックス環境 |
| ストレージ | アプリ本体、履歴、エクスポート、IQキャプチャを保存できる空き容量 |

公式ZIPは`net10.0-windows`／`win-x64`向けのフレームワーク依存パッケージです。ソースからビルドする場合は.NET 10 SDK、CMake、Visual Studio 2022 C++ Build Toolsも必要です。開発環境の詳細は[開発環境](Development-Environment)を参照してください。

Windows on Armでのx64エミュレーション、仮想マシン、USB over IPは公式の主対象ではありません。起動できても、連続したIQ転送やGPU処理の安定性は実機構成に依存します。

## 対応SDR

| 入力 | 必要なソフトウェア | 検出時の要点 |
|---|---|---|
| SDRplay | SDRplay API 3.x。ホストはAPI 3.15を対象 | `sdrplay_api.dll`の64-bit版、APIサービス、他アプリとの排他使用 |
| RTL-SDR | 互換性のある64-bit版`rtlsdr.dll`と`libusb-1.0.dll`、WinUSBドライバー | DLLを`SRdeck.exe`と同じフォルダーへ配置し、正しいUSBインターフェースにWinUSBを設定 |

`Auto`検出はSDRplayを先に、続いてRTL-SDRを試します。複数台を接続する場合や、片方のAPIに問題がある場合は、`Settings`で機種を明示すると切り分けやすくなります。

現在、HackRF、Airspy、RX-888、一般的なWAV／RAW IQファイルは公開UIの入力源として実装されていません。

## サンプルレートとPC性能

画面で選択できる`RATE`は1.6、2、4、6、8 MS/sです。RATEを上げると広い帯域を一度に扱えますが、USB転送量、チャネル抽出、FFT、描画の負荷も増えます。

必要性能は、次の条件の組み合わせで大きく変わります。

- `RATE`とFFT解像度
- CPU FFTまたはGPU FFT
- プラグインが同時処理するチャネル数
- 地図、一覧、ウォーターフォールの更新量
- IQキャプチャや履歴書き込みの頻度

初回は2 MS/s、8K FFT、監視チャネル1つ程度から始めてください。必要な帯域が入らない場合にRATEを上げ、`PRC`、`FFT`、`WPF`を見ながら調整すると原因を分離できます。ADS-Bはホスト入力に2 MS/s以上を要求します。

## GPUと表示

GPU FFTは任意です。利用できない、またはドライバーとの相性が悪い場合は`Settings`でCPU FFTへ切り替えられます。高解像度FFTを使う場合は、対応GPUと最新の安定版ドライバーを推奨します。

地図タブを持つプラグインではMicrosoft Edge WebView2 Runtimeが必要です。地図を開くとLeafletとOpenStreetMapタイルの提供元へHTTPS接続し、IPアドレスと表示地域に対応するタイル座標が送信されます。閉域環境では地図以外の受信・一覧・診断を利用してください。

## 権限と保存先

通常は管理者権限を必要としません。ZIPは`Program Files`直下ではなく、利用者が展開・更新できるフォルダーに置くと扱いやすくなります。設定と履歴は主に次へ書き込まれます。

```text
%LOCALAPPDATA%\SRdeck
```

企業端末のアプリケーション制御、ウイルス対策、Controlled Folder Access、プロキシ設定により、起動、DLLロード、地図、エクスポートが制限されることがあります。

## 導入前チェック

1. WindowsとCPUが64-bitであることを確認します。
2. .NET 10 Desktop Runtime (x64)をインストールします。
3. SDRの64-bitドライバー／APIを準備します。
4. 同じSDRを使う別アプリを終了します。
5. まず低いRATEで検出と連続受信を確認します。
6. 地図を使う場合だけWebView2とネットワーク接続を確認します。

要件を満たしているのに起動や検出ができない場合は[トラブルシューティング](Troubleshooting)へ進んでください。
