# 開発概要

SRdeckは、SDR制御と方式固有処理を公開契約で分離したプラグイン基盤です。新しい復調方式は原則として`SRdeckPlugin.<Name>`へ実装し、ホストへ方式固有の周波数表、DSP、プロトコル、UIを追加しません。

## レイヤーと責務

| レイヤー／プロジェクト | 責務 | 含めないもの |
|---|---|---|
| `SRdeck` | WPFホスト、SDR、FFT、プラグイン管理、共通サービス | 方式固有DSP／プロトコル |
| `SRdeckPlugin.Contracts` | 公開API、DTO、列挙、能力インターフェース | WPF実装、SDKの便利機能 |
| `SRdeckPlugin.Sdk` | ライフサイクル基底、IQ継続性、履歴、設定補助 | 特定方式の規則 |
| `SRdeckPlugin.Wpf` | WPFビュー契約、テーマ、共通コントロール | 方式固有画面 |
| `SRdeckCore.SignalProcessing` | 3方式以上で再利用するDSP部品 | 特定方式だけの同期器 |
| `SRdeckPlugin.*` | 周波数、DSP、FEC、プロトコル、状態、UI | ホスト内部ViewModelへの依存 |
| `SRdeck.Tests` | ホスト／プラグイン横断の回帰ハーネス | 公開パッケージのランタイム機能 |

APIの型形状は`SRdeckPlugin.Contracts`、意味と適合条件は`docs/plugin-interface-specification.md`、方式固有動作は各プラグイン実装とテストが正本です。

## 検出とロード

ホストは起動時に`AppContext.BaseDirectory`を非再帰で検索し、`SRdeckPlugin.*.dll`に一致するアセンブリを候補にします。

エントリクラスは次を満たします。

- public
- 非abstract
- 非generic
- `IPluginModule`を実装
- publicな引数なしコンストラクターを持つ
- 安定した一意のDescriptor IDを返す
- Host API範囲に現在のAPI版を含める

アセンブリ名が一致しても、型ロード、依存DLL、Descriptor、API互換性が不正なら利用可能にはなりません。プラグイン専用AssemblyLoadContextはなく、ホストと同じ既定コンテキストを共有します。

## Descriptor

`PluginDescriptor`は発見時にホストが利用する契約です。

- `Id`: 永続化に使う安定ID。表示名やアセンブリ名とは別
- `DisplayName`: MODEなどに表示する名称
- `Description`: 利用者向けの短い用途
- `PluginVersion`: プラグイン自身の版
- `MinimumHostApiVersion`／`MaximumHostApiVersion`: 対応API範囲
- `Capabilities`: 実装する任意機能
- `Provider`／`License`: 提供元とライセンス

IDは`^[a-z0-9]+(?:[.-][a-z0-9]+)*$`に一致させ、公開後に別プラグインへ再利用しません。能力フラグは実際のインターフェースと一致させます。

## ライフサイクル

通常の呼び出し順は次です。

```text
Discover
  -> InitializeAsync(hostContext)
  -> ActivateAsync()
  -> StartStreamAsync()
  -> ConsumeAsync(...) 0..N回
  -> StopStreamAsync()
  -> DeactivateAsync()
  -> DisposeAsync()
```

実装上の重要点:

- 初期化とアクティブ化を区別する
- Startごとにストリーム依存DSP状態を初期化する
- Stop、Deactivate、Disposeを複数回呼ばれても安全にする
- Start途中の例外でも確保済み資源を解放する
- CancellationTokenを尊重する
- UIがなくてもヘッドレスで動作できる設計を検討する
- Dispose後の処理を拒否する

SDKの`PluginModuleBase`は状態遷移、直列化、失敗時クリーンアップ、リセット登録を共通化します。独自にライフサイクル状態機械を複製する前にSDKを確認してください。

## IQ入力を選ぶ

### 生IQ

`IIqBlockConsumer`はSDRの広帯域IQブロックを受け取ります。

向いている場合:

- 独自の広帯域探索が必要
- ホスト標準チャネル抽出で表せない処理
- 複数の動的チャネルをプラグイン内で探索

代償:

- 周波数変換、フィルター、間引き／リサンプルを自分で所有
- RATE変更や中心周波数変更への対応が必要
- CPU／GPU負荷とコピー量が増えやすい

### 標準チャネルIQ

`IPluginChannelBlockConsumer`は、ホストが同調、フィルター、間引き／リサンプルしたチャネルIQを受け取ります。

向いている場合:

- 既知の中心周波数と帯域
- 方式ごとの復調器を狭帯域IQへ集中させたい
- 複数方式で共通のチャネル抽出品質を使いたい

可能なら標準チャネルIQを選びます。必要チャネルは同調サービスへ要求し、周波数オーバーレイと同じ定義を使います。

## leaseと所有権

`IIqBlockLease`およびチャネルleaseのサンプルは、`ConsumeAsync`コールバック中だけ有効です。

- コールバック後にSpan／Memory／参照を保持しない
- 別スレッドやChannelへ渡すなら、その場で所有バッファへコピー
- poolから借りた配列は明示的に返却
- キャンセル／例外経路でもleaseや所有バッファを解放
- 無制限キューを作らない

「asyncメソッドなのでMemoryを後で使える」とは限りません。契約上の有効期間を守ることが最優先です。

## 継続性とリセット

RATE、中心周波数、ストリーム世代、不連続、ドロップが変わると、DSPの履歴状態をリセットする必要があります。`IqStreamContinuityTracker`はメタデータを比較し、`RequiresReset`を通知します。

リセット対象の例:

- FIR／IIRフィルター状態
- AGC、ノイズ推定
- タイミング／搬送波同期
- デインターリーバー、FEC蓄積
- 再構成中フレーム
- 音声リサンプラー
- プリトリガー履歴

SDKの`RegisterStreamReset`へリセット処理を登録し、Stop、再Start、不連続で同じ基準を使います。

## ホストサービス

`IPluginHostContext`から、プラグイン専用のサービスを利用します。

| サービス | 用途 |
|---|---|
| Logger | プラグインIDで分離された診断 |
| Settings | `settings.json`とデータディレクトリ |
| Tuning | 周波数／帯域／複数チャネル要求 |
| Audio | 共通音声ルーターへのPCM出力 |
| UI Dispatcher | WPFスレッドへのマーシャリング |
| Metrics | 処理量、ドロップ、時間、品質 |
| Notifications | ホスト向け状態／結果通知 |

ホストのDIコンテナー、内部ViewModel、具体的SDRコントローラーへ直接依存しません。公開契約にないホスト型を参照すると、ビルド時に動いても配布互換性を失います。

## 任意能力

| インターフェース | 目的 |
|---|---|
| `IPluginViewProvider` | メイン／設定WPFビュー |
| `IPluginProfileProvider` | 静的プロファイル |
| `ILivePluginProfileProvider` | 受信中に切替可能なプロファイル |
| `IFrequencyOverlayProvider` | スペクトラムのチャネルマーカー |
| `IWaterfallAnnotationProvider` | ウォーターフォール注釈 |
| `IWaterfallDisplayProvider` | 表示期間などの要求 |
| `IPluginResultProvider` | ホスト向け結果サマリー |
| `IPluginExportProvider` | CSV／JSONなどのエクスポート |
| `IPluginProcessingDiagnosticsProvider` | 入力から復号までの診断 |
| `IPluginProcessingWarmup` | 受信開始前の準備 |

能力を追加したらDescriptor、実装、テスト、READMEを同じ変更で更新します。

## 設定とデータ

`context.Settings.DataDirectory`は通常次です。

```text
%LOCALAPPDATA%\SRdeck\plugins\<plugin-id>
```

設定は型付きで正規化し、範囲外、欠落、旧版フィールドを安全な既定値へ変換します。履歴は最大件数を持たせ、書き込み失敗がリアルタイムDSPを停止させないよう分離します。

## スレッドとUI

IQコールバックで重いI/OやWPF更新を行いません。DSPはUI非依存の状態を更新し、表示用スナップショットを一定間隔でDispatcherへ渡します。

- ObservableCollectionはUIスレッドで更新
- 高頻度メトリクスを毎サンプル通知しない
- UIが閉じていてもDSPが動く
- Viewの再生成でストリーム状態を失わない
- Stop時にバックグラウンドタスクを完了／キャンセル

## 互換性と失敗隔離

ホストとプラグインは同じプロセスです。未処理例外、デッドロック、無制限メモリ、ネイティブクラッシュはホスト全体へ影響します。

- 公開APIだけを参照する
- ホストと同じプラットフォームパッケージ版を使う
- 入力値、ファイル、フレーム長を検証する
- 例外を状態とログへ変換し、リアルタイムコールバックから漏らさない
- ネイティブ資源、音声、Timer、CancellationTokenSourceを確実に破棄する
- 停止不能よりデータ欠落を選ぶ有界キューを設計する

## 読む順序

1. [標準サービス設計](https://github.com/sake846/SRdeck/blob/main/docs/plugin-standard-services-architecture.md)
2. [プラグインAPI仕様](https://github.com/sake846/SRdeck/blob/main/docs/plugin-interface-specification.md)
3. [開発・配布ガイド](https://github.com/sake846/SRdeck/blob/main/docs/plugin-development-guide.md)
4. [右ペインUIデザイン指針](https://github.com/sake846/SRdeck/blob/main/docs/plugin-right-pane-design-guidelines.md)
5. [回帰試験仕様](https://github.com/sake846/SRdeck/blob/main/docs/regression-test-specification.md)
6. [最初のプラグイン](Creating-First-Plugin)
