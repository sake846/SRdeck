# 開発概要

SRdeckは、ホストと方式別プラグインを公開契約で分離しています。新しい復調方式は原則として`SRdeckPlugin.<Name>`へ実装し、ホストへ方式固有コードを追加しません。

## 主なプロジェクト

| プロジェクト | 役割 |
|---|---|
| `SRdeck` | WPFホスト、SDR制御、FFT、プラグイン実行 |
| `SRdeckPlugin.Contracts` | 公開APIのコンパイル上の正本 |
| `SRdeckPlugin.Sdk` | ライフサイクル基底クラス、IQ継続性、設定／履歴などの補助 |
| `SRdeckPlugin.Wpf` | WPFビュー契約、共通テーマとコントロール |
| `SRdeckCore.SignalProcessing` | 方式横断のDSP部品 |
| `SRdeckPlugin.*` | 方式固有のDSP、プロトコル、状態、UI |
| `SRdeck.Tests` | コンソール型の回帰試験ハーネス |

## 検出条件

ホストは起動時に`AppContext.BaseDirectory`、つまり通常は`SRdeck.exe`と同じフォルダーを非再帰で検索します。対象は`SRdeckPlugin.*.dll`です。

エントリクラスは、public、非abstract、非genericで、`IPluginModule`を実装し、publicな引数なしコンストラクターを持つ必要があります。

## ライフサイクル

通常の順序は次のとおりです。

```text
Discover
  -> InitializeAsync(hostContext)
  -> ActivateAsync()
  -> StartStreamAsync()
  -> ConsumeAsync(...)
  -> StopStreamAsync()
  -> DeactivateAsync()
  -> DisposeAsync()
```

`StopStreamAsync`、`DeactivateAsync`、`DisposeAsync`は複数回呼ばれても安全にしてください。SDKの`PluginModuleBase`は状態遷移、直列化、失敗時のクリーンアップを共通化します。

## IQの受け取り方

- `IIqBlockConsumer`: SDRの生IQブロックを受け取る
- `IPluginChannelBlockConsumer`: ホストが周波数変換、フィルター、間引き／リサンプルした標準チャネルIQを受け取る

可能なら標準チャネルIQを使い、方式固有コードが同じチャネル抽出処理を重複実装しないようにします。lease内のサンプルはコールバック中だけ有効です。後続の非同期処理へ渡す場合は、コールバック中に所有バッファへコピーしてください。

## ホストサービス

`IPluginHostContext`から、プラグイン専用ロガー、設定ストア、チューニング、音声、UIディスパッチャー、メトリクス、通知などを利用できます。ホストのDIコンテナーや内部ViewModelへ直接依存しないでください。

## 任意機能

能力フラグと実装インターフェースを一致させます。代表例は次のとおりです。

- `IPluginViewProvider`: WPFメイン／設定ビュー
- `IPluginProfileProvider`、`ILivePluginProfileProvider`: 動作プロファイル
- `IFrequencyOverlayProvider`: スペクトラムの周波数マーカー
- `IWaterfallAnnotationProvider`、`IWaterfallDisplayProvider`: ウォーターフォール表示
- `IPluginResultProvider`: ホスト向け結果サマリー
- `IPluginExportProvider`: エクスポート
- `IPluginProcessingDiagnosticsProvider`: 処理段階の診断

## 正本となる文書

1. [標準サービス設計](https://github.com/sake846/SRdeck/blob/main/docs/plugin-standard-services-architecture.md)
2. [プラグインAPI仕様](https://github.com/sake846/SRdeck/blob/main/docs/plugin-interface-specification.md)
3. [開発・配布ガイド](https://github.com/sake846/SRdeck/blob/main/docs/plugin-development-guide.md)
4. [右ペインUIデザイン指針](https://github.com/sake846/SRdeck/blob/main/docs/plugin-right-pane-design-guidelines.md)
5. [回帰試験仕様](https://github.com/sake846/SRdeck/blob/main/docs/regression-test-specification.md)

APIのシグネチャは`SRdeckPlugin.Contracts`の公開型を最終的な正本とします。
