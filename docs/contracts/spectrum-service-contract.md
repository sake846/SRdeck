# 共有スペクトラムサービス

`IPluginHostContext.Spectrum` は、既存のホストFFTを観測・可視化・周波数解析へ再利用する任意の標準サービスです。
受信レベル通知の `ReceiverTelemetry` とは分離しています。契約は `SRdeckPlugin.Contracts/SpectrumContracts.cs`、
ホスト実装は `HostSpectrumService` にあります。WPF、個別プラグイン、しきい値、履歴保持時間を契約へ含めません。

## 問い合わせ

`GetSnapshot(PluginSpectrumRequest)` は、取得範囲・出力点数・集約方法を指定して最新の有効な表示用スペクトラムを取得します。
同調、入力レート、ホストFFTの分解能や窓を変更する操作ではありません。

| 指定 | 意味 |
|---|---|
| `InputBand` | 実際に取り込んだ入力帯域全体 |
| `DisplayBand` | スペクトラム画面のズーム・パン後の中心周波数と表示帯域幅 |
| `FrequencyRange` | `LowerFrequencyHz` 以上、`UpperFrequencyHz` 未満の任意の絶対周波数範囲 |
| `BinCount` | 出力セル数。1〜16,384。プラグイン側の描画や分析に応じて選択 |
| `Peak` | 各セルに含まれる表示用スペクトラム値の最大値 |
| `MeanPower` | 各セルの表示用スペクトラム値を線形電力へ戻して平均し、dBmへ変換 |

`MeanPower` はセルと重なる表示用ビンを等重みで扱う電力平均です。元のFFTがすでに表示用に集約されている場合があるため、
アンテナ端の受信帯域積分電力やdBm/Hzを表すものではありません。後者の用途をこのAPIの数値で代用しません。

任意範囲には有限で増加する両端が必要です。他の範囲指定へ明示的な両端を併記することはできません。
不正な点数、列挙値、範囲は `ArgumentException` 系で拒否します。未対応ホストや有効FFTの生成前は `null` です。

## 結果の意味と所有権

- 各値はスペクトラム／ウォーターフォールと同じ校正基準のdBmで、低周波から高周波へ等間隔に並びます。
- `LowerFrequencyHz` / `UpperFrequencyHz` は要求した表示座標を保持します。入力帯域外を切り落として横軸を変えません。
- セルの区間は半開区間です。入力帯域と一部だけ重なるセルは取得できた部分を集約し、まったく重ならないセルは `NaN` です。
- `InputCenterFrequencyHz` / `SampleRateHz` はFFTの実入力条件です。画面をパンしても入力中心へすり替わりません。
- `SourceBinCount` は本サービスで再集約する前の表示用ビン数で、元のFFT点数とは限りません。
- `CalibrationOffsetDb` は表示用FFT値へ適用した校正差です。`TimestampUtc` はFFTの公開時刻で、機器のサンプル取得時刻ではありません。
- `FrameId` は元FFTの識別子です。同じFFTでも表示範囲・点数・集約・校正が変われば問い合わせ結果は変わります。
- 返却メモリはコピー済みで、呼び出し後も保持できます。Disposeやリース解放は不要です。ホストの再描画やバッファ再利用で値は変わりません。

ホストはプラグインコンテキストごとに直近1問い合わせの結果を再利用し、キャッシュを無制限に増やしません。
結果の重複排除、しきい値判定、履歴、描画は消費側の責務です。表示へ追従する消費側は、`FrameId` だけでなく
範囲や集約条件の変更も確認します。固定周波数で監視したい消費側は `FrequencyRange` を利用できます。

## 利用例

```csharp
// 全受信帯域のピーク表示。
var full = context.Spectrum.GetSnapshot(new(1024));

// メイン画面のズーム・パンに追従する独自グラフ。
var display = context.Spectrum.GetSnapshot(new(512, PluginSpectrumRegion.DisplayBand));

// 画面の操作から独立した周波数範囲の表示用電力平均。
var band = context.Spectrum.GetSnapshot(new(128,
    PluginSpectrumRegion.FrequencyRange, PluginSpectrumAggregation.MeanPower,
    LowerFrequencyHz: 144_000_000, UpperFrequencyHz: 146_000_000));
```

非対応の `IPluginHostContext` 実装は既定の `NullPluginSpectrumService` を利用するため、既存プラグインへ
サービス実装を義務付けません。共有FFTがないことだけで架空の校正値や0 dBmを返してはなりません。
