# 再生ライフサイクル回帰試験

再生経路の回帰試験は、入力データ、同期条件、観測値を固定して実行する。試験名は `SRdeck.Tests` の回帰試験カタログに登録し、機能拡張時にカタログの完全性検査で取りこぼしを検出する。

## 試験契約

- `PlaybackProcessor` は 1 回の読込で得た IQ バイト列を I/Q の `short` 配列へ分離し、リーダーのゲインと周波数を処理コールバックへ渡す。周波数が 0 以下の場合は要求のフォールバック周波数を使う。
- 連番ファイルへ切り替わったブロックでは、切替前後のファイル名を比較して `NotifyFileChanged` を 1 回だけ呼ぶ。通知内容は切替後のファイル名とする。
- ファイル終端ではリーダーが停止していても出力バッファの残量が 0 になるまで待ち、その後に `PlayingFile` を停止する。読込例外または処理例外は `SdrErrorMessage` を発行して再生状態を停止する。
- 再生開始は、既存セッション停止、ファイルオープン、入力セッション開始、サンプルレート同期、出力開始の順序を守る。オープン失敗、セッション開始拒否、出力開始例外では、ファイル、入力セッション、音声出力を残さず停止する。
- `PlaybackSessionRunner` は世代番号が現行の場合だけ停止と終了通知を行う。遅延したコールバックは操作ゲートを取得するまで状態を変更せず、世代が古ければ破棄する。
- `AudioService.Shutdown` は音声出力を停止してバッファを消去し、出力とファイルリーダーを破棄する。各リソースの停止・消去・破棄回数を観測する。

## 登録試験

| カタログ名 | 主な確認内容 |
| --- | --- |
| `Playback processor end drain and errors` | IQ 分離、ゲイン／周波数、ファイル切替通知、終端ドレイン、読込／処理例外、状態停止 |
| `Playback session starter compensation` | オープン失敗、入力セッション開始拒否、出力開始例外、各リソースの補償停止 |
| `Playback session runner generation and failure` | 正常終了、ループ例外、遅延コールバック、世代不一致、終了通知回数 |
| `Audio service shutdown releases playback resources` | 出力停止・バッファ消去・出力破棄、ファイルリーダーのクローズ・破棄 |

実行例:

```powershell
dotnet build SRdeck.Tests\SRdeck.Tests.csproj -c Release --no-restore -p:OS=Unix
dotnet run -c Release --no-build --project SRdeck.Tests\SRdeck.Tests.csproj -- --run-test "Playback processor end drain and errors"
```

実機音声デバイスや長時間再生を必要とする受入確認は、この決定的な直接試験とは別に、対象環境、再生時間、期待する停止・再オープン結果、ログ保存先を記録して実施する。
