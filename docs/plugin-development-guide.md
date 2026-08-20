# SDRプラグイン開発・配布ガイド

## 1. 目的と適用範囲

この文書は、`docs/plugin-interface-specification.md` で定義した現行契約の実装ガイドである。
基幹のスペクトラム、ウォーターフォール、SDR制御、開始・停止処理を変更せず、
多様な受信・復調・解析方式を独立DLLとして追加するために使う。
標準サービスと個別プラグインの所有権は
[SDRプラグイン標準サービス設計](plugin-standard-services-architecture.md) に従う。

現行版は次の制約を持つ。

- プロセス内DLLとしてロードする。プロセス境界の障害隔離は行わない。
- 主プラグインに加えて、現在の同調通過帯域へ収まる追加プラグインを同時実行できる。
- 実行中のDLL追加・更新（ホットリロード）は行わない。再起動時に再探索する。
- WPF UIは任意であり、ヘッドレス対応プラグインはWPFへ依存しないこと。

## 2. 実装済みプロジェクト境界

| プロジェクト | ターゲット | 責務・参照方向 |
|---|---|---|
| `SRdeckPlugin.Contracts` | `net10.0` | 安定契約。基幹、WPF、方式固有型を参照しない |
| `SRdeckPlugin.Wpf` | `net10.0-windows` | 任意のWPFビュー契約。Contractsのみ参照 |
| `SRdeckCore.SignalProcessing` | `net10.0` | 共有Radix-2 FFT、複素数、IQリング、NCO、CIC、polyphaseリサンプラなど方式非依存DSP |
| `SRdeckPlugin.Sdk` | `net10.0` | 共通ライフサイクル、連続性追跡、診断、JSON Lines履歴、IQ WAVキャプチャ、ベンチマーク支援 |
| `SRdeck.Tests` | `net10.0-windows` | 基幹・プラグイン共通の回帰試験 |
| `SRdeckPlugin.<方式名>` | `net10.0` または `net10.0-windows` | 方式固有DSP、解析、設定、履歴、任意UI。別プラグインを参照しない |
| `SRdeck` | `net10.0-windows` | SDR制御、基幹画面、プラグインホスト |

`SRdeck` は内蔵プラグインをコンパイル参照せず、ビルド・同梱依存としてだけ扱う。
したがって基幹ソースから個別方式の具象型を利用できない。
内蔵プラグインDLLはビルド後にアプリ出力ディレクトリへコピーされ、外部プラグインと同じ探索経路でロードされる。

## 3. DLL探索とエントリポイント

起動時に `PluginModuleCatalog` がアプリのベースディレクトリにある
`SRdeckPlugin.*.dll` を探索する。各DLLから、次の条件を満たす型を登録する。

- `IPluginModule` を実装する。
- `public`、非抽象、非ジェネリックの具象型である。
- `public` な引数なしコンストラクターを持つ。
- `Descriptor.Id` がASCII小文字、数字、ピリオド、ハイフンだけで構成される。
- ホストAPI `1.0` が記述子の最小・最大互換バージョン範囲内にある。

保存済みの選択IDがない場合だけ、`Descriptor.IsEnabledByDefault` が選択候補の優先順位に使われる。
これは固定モードではなくプラグイン自身のメタデータであり、保存済みIDは常に優先される。

新しい外部プラグインは `SRdeckPlugin.Contracts` を参照する独立プロジェクトとして作成し、
生成したDLLとその固有依存DLLをアプリの実行ファイルと同じディレクトリへ配置する。
基幹のC#コード、XAML、列挙型、switch文への登録追加は不要である。

### 3.1 プロジェクトの作成

リポジトリ内で開発する最小のヘッドレスプロジェクトは次の形にする。

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\SRdeckPlugin.Contracts\SRdeckPlugin.Contracts.csproj" />
    <ProjectReference Include="..\SRdeckPlugin.Sdk\SRdeckPlugin.Sdk.csproj" />
  </ItemGroup>
</Project>
```

WPFビューを提供する場合だけ `TargetFramework` を `net10.0-windows`、`UseWPF` を `true` とし、
`SRdeckPlugin.Wpf` を追加参照する。方式非依存DSP部品が必要な場合は
`SRdeckCore.SignalProcessing` を参照できる。ヘッドレス専用プラグインはWPFを参照しない。

方式固有の型は、プロジェクト名と同じルート名前空間 `SRdeckPlugin.<Name>` またはその子名前空間へ置く。
基幹所有に見える `SRdeck.*`、別プラグイン、共有Contracts／SDKの名前空間へ方式固有型を宣言してはならない。
共有ライブラリの型を `using` して利用すること自体は、この所有規則へ違反しない。

リポジトリ外で開発する場合、ホストと同じ版の `SRdeckPlugin.Contracts.dll` と、使用する場合だけ
`SRdeckPlugin.Sdk.dll`／`SRdeckPlugin.Wpf.dll` を参照する。現時点ではNuGetパッケージを公開していないため、
対象ホストの配布物または同じタグのビルド成果物を使用する。Contractsの版を別ホストから混在させない。

コンパイル可能な例は [ヘッドレス生IQスターター](samples/SRdeckPlugin.Example/README.md) と
[標準チャネルIQスターター](samples/SRdeckPlugin.ChannelExample/README.md) を参照する。

### 3.2 DLL名、依存関係、配布

- エントリアセンブリ名は探索パターンに一致する `SRdeckPlugin.<Name>.dll` とする。
- プラグイン固有の管理DLLとネイティブDLLは、ホスト実行ファイルと同じディレクトリまたは依存側が要求する配置へコピーする。
- 現行版は既定の `AssemblyLoadContext` を共有する。別プラグインやホストと同名・異版の依存DLLを同梱すると競合し得るため、依存バージョンをホスト配布物と照合する。
- 現行配布はWindows x64を基準とする。ネイティブ依存を持つ場合はRID、CPU命令セット、欠落時の動作をREADMEへ記録する。
- 第三者コード、参照データ、ネイティブランタイムを同梱する場合は、ライセンス本文、帰属、ソース入手先をプラグイン固有の `THIRD-PARTY-NOTICES.md` に記載してDLLと同梱する。
- DLL追加・更新後はアプリを再起動する。現行版はホットリロードとアンロードを行わない。

## 4. 最小プラグイン

最低限 `IPluginModule` と不変の `PluginDescriptor` を実装する。
通常は `SRdeckPlugin.Sdk` の `PluginModuleBase` を継承し、方式固有処理だけをフックへ実装する。
基底クラスがホストID検証、キャンセル、ライフサイクル操作の直列化、状態遷移、開始順序、
失敗時の補償後処理、冪等な破棄を統一する。内蔵プラグインはすべてこの基底クラスを使用する。

```csharp
using SRdeckPlugin.Contracts;
using SRdeckPlugin.Sdk;

public sealed class ExamplePlugin : PluginModuleBase, IIqBlockConsumer
{
    public override PluginDescriptor Descriptor { get; } = new(
        "example.decoder", "Example", "Example decoder",
        new Version(1, 0), new Version(1, 0), new Version(1, 0),
        PluginCapabilities.IqConsumer | PluginCapabilities.Headless,
        "Example provider", "License name");

    public PluginIqPreferences IqPreferences { get; } = new(4);

    protected override ValueTask OnStartStreamAsync(CancellationToken token)
    {
        // 方式固有の受信状態をリセットする。
        return ValueTask.CompletedTask;
    }

    public ValueTask ConsumeAsync(IIqBlockLease block, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (State != PluginLifecycleState.Streaming)
            return ValueTask.CompletedTask;
        // block.Samplesはこの呼び出しが完了するまでだけ有効。
        return ValueTask.CompletedTask;
    }
}
```

`PluginModuleBase` のフックから見える状態は、次の規則に従う。

- `OnInitializeAsync` と `OnActivateAsync` は遷移前の状態で呼ばれ、正常終了後にそれぞれ
  `Initialized`、`Active` へ遷移する。
- Activateフックが失敗した場合は `Initialized` を維持し、キャンセルされない共通経路で
  Deactivateフックを呼んで部分的な有効化を取り消す。
- `OnStartStreamAsync` は `Active` で呼ばれ、正常終了後に `Streaming` へ遷移する。
  Startフックまたは開始後フックが失敗した場合は `Active` を維持し、Stopフックで補償停止する。
- `OnStopStreamAsync` は新しいIQブロックを拒否するため、先に `Active` へ遷移してから呼ばれる。
  フックが失敗しても `Streaming` へ戻さず、後処理を次のStopまたはDisposeから再試行できる。
- `Streaming` 中に `DeactivateAsync` が直接呼ばれた場合、基底クラスがStopフックを完了してから
  `Initialized` へ遷移し、`OnDeactivateAsync` を呼ぶ。Deactivateフックが失敗しても
  `Active` へ戻さず、後処理を再試行できる。
- `OnDisposeAsync` は `Disposed` へ遷移してから1回だけ呼ばれる。同時または反復したDispose呼び出しは、
  同じ破棄処理の完了または例外を共有する。StreamingまたはActiveから直接Disposeした場合も、
  Stop、Deactivate、Disposeの順に全後処理を試行し、複数の失敗は集約して返す。
- `OnActivatedAsync`、`OnStreamStartedAsync`、`OnStreamStoppedAsync`、`OnDeactivatedAsync` は、
  対応する主フックと状態遷移が正常終了した後の通知用である。後処理が不要なら主フックだけを実装する。
- 基底クラスはStart前、Stop後、Deactivate後、Dispose後にプラグイン別AudioキューをResetする。
  音声設定変更やIQ不連続など、ストリーム中のResetだけを方式側で実装する。
- プリトリガーバッファや音声世代などストリーム単位の状態は、モジュールのコンストラクターから
  `RegisterStreamReset` へReset処理を登録する。基底クラスがStart前とStop後（開始失敗時の補償停止を含む）に
  登録順で実行するため、Start/Stopフックで同じResetを重複して呼ばない。
- 音声世代の判定には `PluginAudioGenerationTracker` を使用し、その `Reset` を
  `RegisterStreamReset` へ登録する。IQ不連続やストリーム中のチャネル切替時は方式側から明示的にResetする。

IQ処理とStop/Disposeが並行するプラグインは、最初の `State` 判定だけに依存してはならない。
方式固有の処理ロックを取得した直後にも `State == PluginLifecycleState.Streaming` を再確認し、
停止開始前に待機していた遅延ブロックを処理しないこと。基底クラスのライフサイクルゲートは
IQホットパスでは取得されないため、この規則による定常DSP性能への追加ロックはない。
内部ワーカーやキューへIQを渡す方式は、Stopフックで新規投入を拒否してから、処理中ブロックの完了を待ち、
残キューを排出する。破棄が必要な方式固有の理由がある場合だけ、その理由とデータ損失範囲を文書化する。
Stop復帰後に旧ストリームの結果を発行してはならない。

現在のホスト条件では受信を開始できないがプラグイン自体は正常な場合、`OnActivateAsync` から
`PluginActivationRejectedException` を送出する。例えば最小入力レート不足や必須同調の拒否が該当する。
設定不一致を一般例外で表して恒久的なプラグイン障害と混同しない。

## 5. 能力別インタフェース

| 用途 | 実装・利用する契約 | 注意事項 |
|---|---|---|
| IQ入力 | `IIqBlockConsumer` | リースを保持しない。必要なら呼出し中に自前メモリへコピーする |
| 標準チャネルIQ | `IPluginChannelBlockConsumer` | `ChannelIqConsumer`を宣言し、周波数、帯域、出力レートを要求する |
| 動作プロファイル | `IPluginProfileProvider` | IDは永続化されるため変更・再利用しない |
| ストリーム中のプロファイル変更 | `ILivePluginProfileProvider` | 安全な再同調・状態リセット・設定ロールバックを実装できる場合だけ宣言する |
| 同調 | `IPluginHostContext.Tuning` | 承認、調整、拒否を必ず処理し、実適用通知を購読できる。サンプルレートは基幹の設定値を使用する |
| 設定・データ | `IPluginHostContext.Settings` | プラグインIDで分離されたディレクトリだけを使用する |
| 音声 | `IPluginHostContext.Audio` | PCMにプラグインID、IQストリームID、連番を設定する |
| 受信通知音 | `IPluginHostContext.Notifications` | 受信判断だけを行い、波形生成、遅延再生、フォールバックは基盤へ委譲する |
| UIスレッド | `IPluginHostContext.Dispatcher` | DSPスレッドからWPFオブジェクトを直接更新しない |
| 右ペイン・設定UI | `IPluginViewProvider`（`SRdeckPlugin.Wpf`） | ヘッドレス実行時はビューを生成しない |
| 周波数表示 | `IFrequencyOverlayProvider` | Hz単位のスナップショットを返し、基幹描画型を参照しない |
| ウォーターフォール注釈 | `IWaterfallAnnotationProvider` | 入力ストリーム時計による時刻・Hz単位の位置を返し、基幹に座標変換を委ねる |
| ウォーターフォール表示要求 | `IWaterfallDisplayProvider` | `WaterfallDisplayRequest`で時間モードと希望表示帯域幅を返す。基幹が対応範囲へ調整する |
| 共通結果通知 | `IPluginResultProvider` | 詳細はプラグイン所有とし、共通概要とバージョン付きJSONだけを通知する |
| エクスポート | `IPluginExportProvider` | 対応形式を安定IDで列挙し、キャンセル可能な非同期処理にする |
| 方式固有処理経路の診断 | `IPluginProcessingDiagnosticsProvider` | 実行先と処理範囲を申告し、ホストが推測しなくてよいようにする |
| 起動時DSP準備 | `IPluginProcessingWarmup` | 内蔵プラグインの標準契約。ゼロIQなどで遅延初期化を済ませ、結果・履歴・統計を公開せず、実受信前に状態をリセットする。標準チャンネル入力では `PluginProcessingWarmup.RunChannelAsync` を使用する |

能力フラグは、実装した任意契約と一致させる。未知の能力ビットはホスト側で無視される。
`PluginCapabilities.Headless` は別メンバーではなく能力フラグで宣言する。`ILivePluginProfileProvider`、
`IPluginProcessingDiagnosticsProvider`、`IPluginProcessingWarmup` は現行版では専用の能力ビットを持たない。

現在のホスト構成では受信機レベルを `IPluginHostContext.ReceiverTelemetry`、配送キューと処理時間を
`RuntimeDiagnostics`、方式固有メトリクス記録を `Metrics` から取得できる。これらは利用可能性または
Null実装を考慮し、DSPの成立条件として必須にしない。

`WaterfallTimeMode` は `Uncompressed`（FFT更新1回につき1行、時間方向の集約なし）、
`ThreeMinutes`、`OneHour` の3値を持つ。プロバイダーがない場合および未定義値の場合は
`ThreeMinutes` とし、`PreferredDisplayBandwidthHz` が `null` の場合は拡大しない。
無集約時の時間目盛りは、ウィンドウ履歴が90秒以内なら5秒間隔、90秒を超える場合は10秒間隔で表示する。
希望帯域幅は強制値ではなく、基幹が入力サンプルレートと最小表示幅の範囲へクランプする。
時間モードや表示帯域の具体値は方式固有要件としてプラグイン側で定義する。

`IFrequencyOverlayProvider` の受信帯域表示で `FrequencyOverlayItem.Fill` と `Stroke` を指定する場合は、
`SRdeckPlugin.Wpf` の `PluginReceiverBandColors.Primary` /
`PluginReceiverBandColors.Secondary` と `WithAlpha` を使う。
これらはスペクトラム上の帯域識別専用色であり、右ペインの共通スタイルである
`PluginDisplayAccentPrimaryBrush` / `PluginDisplayAccentSecondaryBrush` /
`PluginDisplayAccentTertiaryBrush`（主・副・補助表示アクセント）とは分離する。
共通スタイル色を帯域オーバーレイへ流用しない。ラベル色や透明度は帯域の選択状態・重なり方に応じて個別に決める。

## 6. IQ配送と障害隔離

- IQは正規化済み `Complex32` として、アクティブかつStreaming状態のプラグインだけへ配送される。
- 同一入力ブロックの正規化バッファは参照カウントで共有し、同じ処理条件の標準チャネルも1回だけ生成する。
- メタデータにはストリームID、世代、連番、絶対サンプル位置、単調時刻、UTC時刻、
  サンプルレート、中心周波数、入力元、不連続理由が含まれる。
- 要求キュー容量はホストで1～32へ制限される。満杯時は入力側を待たずにドロップする。
- ドロップ後の次ブロックには `SamplesDropped` が設定される。
- 処理例外は当該プラグインだけをFaultedにし、基幹のSDR、FFT、表示を継続する。
- 追加プラグインの同調要求は現在の主プラグインの通過帯域内だけ承認し、主同調を変更しない。
- ライフサイクル操作の既定上限時間は5秒である。上限超過時も当該プラグインだけを隔離する。

診断スナップショットから、投入・処理・ドロップ数、欠落サンプル、現在・最大キュー深度、
未解放リース、現在・平均・最大処理時間、最終連番、最終成功時刻、最終エラーを取得できる。

### 6.1 標準経路と独自経路

新規プラグインは、標準チャネルサービスが方式要件を満たす場合、周波数変換、帯域制限、レート変換を
独自実装せず標準経路を優先する。`PluginChannelRequest`で安定要求ID、中心周波数、占有帯域幅、
出力サンプルレート、必要なら中間レート範囲、FIRタップ数、CIC段数を宣言する。ホストは
`IChannelIqBlockLease`の同期した集合を`ConsumeChannelsAsync`へ配送し、実際の変換比と入力サンプル単位の群遅延を
`AppliedChannelConfiguration`へ設定する。絶対位置は
`ChannelIqBlockMetadata.MapOutputToSource`で復元する。

`ChannelRequests`は複数要求と実行中のプロファイル変更に対応する。要求IDは集合内で一意かつ安定にし、
複数チャネルの同時監視では、同一原IQブロックから生成された全チャネルを1回のコールバックで処理する。
広帯域入力を段階的に縮小する方式は、第一段出力レート範囲と第二段最大係数を要求できる。

`AccelerationPreference`は`Auto`、`Cpu`、`GpuPreferred`、`GpuRequired`を選べる。GPUバックエンドは
周波数変換だけでなく、ストリーム状態、不連続リセット、群遅延、サンプル位置まで契約全体を満たすものだけを
使用する。`Auto`の選択基準、バックエンド名、校正回数などはホスト実装の運用パラメータであり、
プラグインが依存してはならない。

`GpuPreferred`で適合バックエンドがない場合、ホストはCPUへフォールバックできる。
`GpuRequired`を満たせない場合は要求を拒否するか、`AllowRawIqFallback` が有効なら不連続付き生IQへ
フォールバックできる。いずれの場合も実際の `AppliedChannelConfiguration.ProcessingBackend` と
不連続通知を基準に処理し、要求した実行先が使われたと仮定しない。

互換経路が必要なプラグインは`IIqBlockConsumer`も実装し、`AllowRawIqFallback`を有効にする。
ホストは標準経路を優先し、要求を入力帯域内で構成できない場合だけ生IQへフォールバックする。
チャネル消費処理自体の例外はフォールバックで隠さず、通常のプラグイン障害として隔離する。

チャネライザと方式固有検出器の融合など、標準経路では必要な性能または検出特性を得られない場合は、
理由とベンチマークを方式固有テストへ記録したうえで独自経路を使用してよい。独自経路も共通DSP部品、
連続性追跡、メモリ上限、診断段階および適合試験を可能な限り共有する。

### 6.2 DSP実装規約

- 1サンプルごとのサービス呼び出しを避け、`Span<T>`、`Memory<T>` またはリースされたブロックを使用する。
- short IQ正規化とpolyphase FIRはハードウェア対応時にSIMD経路を使い、非対応CPUでは同じ結果のscalar経路を使う。
- 定常処理で配列を割り当てず、必要な最大一時メモリを診断またはベンチマークで確認する。
- 状態を持つ部品は明示的な `Reset` を提供し、世代、不連続、同調・レート変更時に呼び出す。
- フィルタとレート変換は群遅延および入出力サンプル位置の対応を公開する。
- 共通DSP部品へ方式固有の同期語、フレーム構造、判定閾値を持ち込まない。

### 6.3 SDK補助API

`SRdeckPlugin.Sdk` は必須依存ではないが、次の横断処理を独自実装する前に利用を検討する。

| API | 用途 |
|---|---|
| `PluginModuleBase` | 直列化されたライフサイクル、補償停止、冪等Dispose、ストリーム状態Reset |
| `IqStreamContinuityTracker` | 世代、連番、絶対位置、レート、中心周波数、不連続の変化検出 |
| `PackedIqHistoryBuffer` / `PackedIqHistoryPairBuffer` | 容量上限付きプリトリガーIQ保持 |
| `BoundedIqWavWriter` | 上限時間付き16-bitステレオI/Q WAV保存。診断メタデータはプラグインが別途保存する |
| `PluginAudioGenerationTracker` | 音声ストリームID、世代、連番の追跡 |
| `PluginJsonLinesHistory` / `PluginJsonLinesHistoryWriter<T>` | JSON Lines履歴の読込、追記、保持上限、直列非同期保存 |
| `PluginBenchmark` | ウォームアップを分離した処理時間、割り当て量、実時間倍率の測定 |

SDK補助型も方式固有の上限、ファイル名、履歴スキーマ、エラー表示を決定しない。
プラグインが値を検証し、停止・破棄・保存失敗の動作を適合試験で確認する。

## 7. 設定、履歴、移行

設定はホストが提供するプラグイン別ストアへ、スキーマバージョン付きJSONとして保存する。
データディレクトリはプラグインIDで分離され、`..` などを使った名前空間外アクセスは拒否される。
`PluginSettingsDocument.SecretJsonPaths` は秘密値の分類メタデータであり、現行ホストの
`settings.json` を暗号化しない。PSK、トークン、秘密鍵、パスワードなどを通常設定へ保存してはならない。
Windows Credential ManagerやDPAPIなどOSの資格情報機能へ保存し、設定JSONには不透明な参照だけを保持する。
`SecretJsonPaths` は将来の資格情報ストア移行やUI上のマスキングには利用できるが、保護機能として扱わない。

## 8. UIとヘッドレス

右ペインのレイアウト、文字、色、共通コンポーネント、アクセシビリティは
[プラグイン右ペイン UI デザイン指針](plugin-right-pane-design-guidelines.md) に従う。

GUIではホストの固定スロットへ、選択中プラグインの右ペインと設定ビューを差し替える。
プラグイン未選択、非互換、Faultedも基幹の汎用表示で扱う。
左下ペインのMODEコンボボックスでは、基幹がプラグインIDだけを選択する。AM/FM/SSBなどの
プラグイン固有プロファイルは、右ペインへ提供するプラグイン自身のビューで選択する。必要な停止、切替、再開と
プロファイル設定の保存もプラグインが担当し、基幹は方式固有のUIやIDを持たない。

ヘッドレス起動では `Headless` 能力を持つプラグインだけを選択する。
UI能力とヘッドレス能力を同時に宣言する場合も、ヘッドレス時はビューを生成せずに
初期化・開始・IQ処理・停止・破棄できなければならない。

### 8.1 時系列、一覧、分類

時系列用モデルと、機体やノードなど対象ごとの最新状態・集約モデルを分離する。時系列の 1 要素は 1 回の受信イベント、
一覧の 1 要素は 1 対象の更新可能な現在状態、または 1 分類キーの集約結果とする。同じコレクションを別名のタブへ流用しない。

対象別、種別別の一覧は、元の履歴から表示用コレクションを投影する。分類や検索を切り替えても保存履歴を
変更せず、選択、並べ替え、検索条件を保持する。集計更新を受信イベントごとに全件再構築せず、UIディスパッチを
集約する。大量一覧では行・列仮想化を有効にし、外側の `ScrollViewer` で仮想化を失わせない。

### 8.2 方式固有診断

受信機は UI 型を含まない診断スナップショットを公開し、ViewModel が表示文字列と総合状態へ変換する。
スナップショットには測定時刻、入力・選局、信号、同期、検証・復号のうち該当する段階、累積カウンター、
直近期間の判定に必要な値を含める。

- 総合状態は処理順で最初に成立していない段階を主原因とする。
- 入力条件が正常で結果 0 件の場合は、トラフィックや伝搬待ちを考慮して「監視中」とする。
- 累積棄却数ではなく、直近の評価期間における差分または率で現在状態を判定する。
- 判定閾値は XAML のトリガーへ直書きせず、受信機または診断 ViewModel の名前付き定数にしてテストする。
- UI反映を表示に適した頻度へ集約し、非表示中は表示用整形を停止または低頻度化する。
- 更新が途絶えた場合に検出できるよう、スナップショット時刻と想定更新間隔を保持する。
- コピー、ログ、例外へ受信本文、復号鍵、秘密設定、個人情報になり得る識別子を既定で含めない。

### 8.3 表示モデルと位置情報

時系列は1行1受信イベント、一覧は対象または分類キーごとの更新可能な現在状態とする。
分類軸はUIの固定型ではなく各ViewModelが所有し、方式固有の名称と列はプラグイン側で定義する。

地図では、位置が受信対象、送信元、基地局、推定地点のどれを表すかを明記する。
位置を含む結果がない場合は空の地図だけを表示せず、未受信、選択待ち、または位置非対応の理由を表示する。

## 9. 適合条件と回帰証拠

| 仕様書25章 | 実装証拠 | 主な回帰テスト |
|---:|---|---|
| 1 | DLL探索と引数なし `IPluginModule` 登録 | Plugin DLL discovery |
| 2 | ID選択、プロファイル選択、WPFワークスペース | Plugin profile selection / workspace switching |
| 3 | `PluginIqDispatcher` のActive/Streaming判定 | Plugin IQ dispatch |
| 4 | `IqBlockMetadata` | Plugin IQ dispatch |
| 5 | 有界Channelと非同期ワーカー | Plugin IQ dispatch / built-in plugin IQ paths |
| 6 | IQ・音声・ライフサイクル例外のFault隔離 | Plugin IQ fault isolation |
| 7 | `PluginTuningService` | Plugin tuning arbitration |
| 8 | `AppliedConfigurationChanged` | Plugin tuning arbitration |
| 9 | `JsonPluginSettingsStore` | Plugin namespaced settings |
| 10 | `PluginIqDispatchSnapshot` | Plugin IQ dispatch / IQ fault isolation |
| 11 | ビュー生成を必要としないヘッドレス動作 | Headless plugin lifecycle |
| 12 | CoreEngine/MainViewModel/MainWindowに方式固有参照なし | Host has no concrete plugin assembly references / 最終文字列監査 |
| 13 | UI、履歴、診断、地図、保存、旧データ移行を各プラグインが所有 | 組み込みプラグインの契約・回帰試験 |
| 14 | 複数方式を持つプラグインが同じIQ・音声・ライフサイクル契約を使用 | Profile selection / lifecycle tests |

標準検証コマンドは次のとおり。

```powershell
dotnet restore SRdeck.sln
dotnet build SRdeck.sln -c Release --no-restore
dotnet run --project SRdeck.Tests\SRdeck.Tests.csproj -c Release --no-build
```

## 10. 新規プラグインのレビューチェックリスト

- ID、プロファイルID、設定スキーマIDが安定している。
- Contracts API互換範囲と能力フラグが実装に一致する。
- 基幹プロジェクトや別プラグインを参照していない。
- 標準チャネル処理を利用できるか評価し、独自チャネライザを選ぶ場合は方式要件または性能根拠がある。
- NCO、フィルタ、レート変換、AGC、リングバッファ、IQ WAV書き出しを新規に複製する前に共通DSP・SDKを確認している。
- IQコールバックを待たせず、キューとメモリに上限がある。
- 世代変更、不連続、同調・サンプルレート変更時にDSP状態をリセットする。
- 停止と破棄でイベント、ワーカー、リース、ファイルを解放する。
- 例外とキャンセルを区別し、秘密情報やIQ全体をログへ出さない。
- UIは任意契約に閉じ、ヘッドレス宣言と実際の依存が一致する。
- 設定移行と履歴形式の後方互換テストを用意する。
- Releaseビルド、共通ホスト試験、方式固有回帰試験を通す。
- ブロック境界、実時間倍率、定常割り当て量、群遅延、既知IQベクトルを検証する。

## 11. 組み込みプラグイン固有情報

安定ID、周波数プロファイル、方式固有DSP、プロトコル出典、履歴形式、UI構成、既知ベクトル、
ライセンス帰属などは、それを所有する `SRdeckPlugin.<方式名>` プロジェクト内のREADMEまたは
`docs` ディレクトリへ記録する。本ガイドには個別方式の実装パラメータを重複して持たない。

組み込みプラグインを参照実装として読む場合も、具象クラスや方式固有定数をコピーするのではなく、
Contracts、SDK、共通信号処理の利用方法とライフサイクル境界だけを参照する。
