# 最初のプラグインを作る

ここでは、画面を持たず生IQを受け取る最小プラグインを作ります。完成版は`docs/samples/SRdeckPlugin.Example`にあります。

## 1. プロジェクトを作る

プロジェクト名と出力アセンブリ名を`SRdeckPlugin.<Name>`にします。

```powershell
dotnet new classlib -n SRdeckPlugin.Example -f net10.0
```

リポジトリ内ではContractsとSDKを`ProjectReference`で追加します。外部リポジトリでは、対応するGitHub Releaseから取得した同一バージョンのNuGetパッケージを使います。

## 2. エントリクラスを実装する

```csharp
using SRdeckPlugin.Contracts;
using SRdeckPlugin.Sdk;

namespace SRdeckPlugin.Example;

public sealed class ExamplePlugin : PluginModuleBase, IIqBlockConsumer
{
    private readonly IqStreamContinuityTracker continuity = new();
    private long processedSamples;

    public ExamplePlugin() => RegisterStreamReset(continuity.Reset);

    public override PluginDescriptor Descriptor { get; } = new(
        Id: "example.decoder",
        DisplayName: "Example decoder",
        Description: "Minimal headless raw-IQ plugin",
        PluginVersion: new Version(1, 0),
        MinimumHostApiVersion: new Version(1, 0),
        MaximumHostApiVersion: new Version(1, 0),
        Capabilities: PluginCapabilities.IqConsumer | PluginCapabilities.Headless,
        Provider: "Example provider",
        License: "License name");

    public PluginIqPreferences IqPreferences { get; } = new(4);
    public long ProcessedSamples => Interlocked.Read(ref processedSamples);

    protected override ValueTask OnStartStreamAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Exchange(ref processedSamples, 0);
        return ValueTask.CompletedTask;
    }

    public ValueTask ConsumeAsync(
        IIqBlockLease block,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (State != PluginLifecycleState.Streaming)
            return ValueTask.CompletedTask;

        IqStreamTransition transition = continuity.Observe(block.Metadata);
        if (transition.RequiresReset)
        {
            // フィルター、同期器、遅延処理などをリセットする。
        }

        Interlocked.Add(ref processedSamples, block.Samples.Length);
        return ValueTask.CompletedTask;
    }
}
```

`PluginModuleBase`が公開の非同期ライフサイクルを実装します。方式固有の初期化や終了処理は`OnInitializeAsync`、`OnActivateAsync`、`OnStartStreamAsync`、`OnStopStreamAsync`、`OnDeactivateAsync`などをoverrideします。

サンプルの有効期間は`ConsumeAsync`が戻るまでです。別スレッドや後続タスクで使う場合は、その場でプラグイン所有のバッファへコピーします。

## 3. Descriptorを確認する

- `Id`は永続化される安定IDです。`^[a-z0-9]+(?:[.-][a-z0-9]+)*$`に一致させ、後から再利用やローカライズをしません。
- Host API 1.0を対象にする場合は、最小／最大APIバージョンの範囲に`1.0`を含めます。
- `Capabilities`は、実際に実装した任意インターフェースと一致させます。
- エントリクラスはpublicな引数なしコンストラクターを持つ必要があります。

## 4. ビルドして読み込む

```powershell
dotnet build SRdeckPlugin.Example\SRdeckPlugin.Example.csproj -c Release
```

生成された`SRdeckPlugin.Example.dll`とプラグイン固有の依存DLLを`SRdeck.exe`と同じフォルダーへコピーし、SRdeckを再起動します。`plugins`サブフォルダーへは置きません。

## 5. プロジェクト設定を確認する

生IQを使うヘッドレスプラグインの最小プロジェクトは、次の形にできます。

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>SRdeckPlugin.Example</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\SRdeckPlugin.Contracts\SRdeckPlugin.Contracts.csproj" />
    <ProjectReference Include="..\SRdeckPlugin.Sdk\SRdeckPlugin.Sdk.csproj" />
  </ItemGroup>
</Project>
```

外部リポジトリでは`ProjectReference`を、対象ホストと同じ版の`PackageReference`へ置き換えます。UIを持つ場合は`net10.0-windows`、`UseWPF=true`、`SRdeckPlugin.Wpf`参照を追加します。

## 6. 生IQか標準チャネルIQかを決める

この例は`IIqBlockConsumer`で生IQを受け取りますが、既知周波数の狭帯域方式では`IPluginChannelBlockConsumer`を推奨します。

| 選択 | 向いている処理 | 自分で所有するもの |
|---|---|---|
| 生IQ | 広帯域探索、独自チャネル化 | 周波数変換、フィルター、リサンプル |
| 標準チャネルIQ | 既知中心／帯域の復調 | 方式固有の同期、復調、解析 |

標準チャネル例は`docs/samples/SRdeckPlugin.ChannelExample`にあります。同調要求、チャネルID、中心周波数、帯域、出力サンプルレートを一つの定義から作り、周波数オーバーレイとも一致させてください。

## 7. コールバックを実処理へ育てる

`ConsumeAsync`では、次の順序を保つとテストしやすくなります。

1. キャンセル、Dispose、Streaming状態を確認する。
2. IQメタデータの継続性を確認する。
3. 不連続時にフィルター、同期器、フレーム蓄積をリセットする。
4. leaseが有効な間に同期処理を行う。
5. 後続処理が必要なら所有バッファへコピーする。
6. メトリクスを更新し、例外をホストへ漏らさない。

リアルタイムコールバックでファイルI/O、WPF更新、無制限キューへの書き込みを行わないでください。処理が追いつかない場合に古いブロックをどう扱うかを、有界キューとメトリクスで明示します。

## 8. 設定を追加する

設定は不変recordにし、読み込み後に範囲と互換性を正規化します。

```csharp
public sealed record ExampleSettings(
    long FrequencyHz = 145_000_000,
    int MaximumHistory = 10_000)
{
    public ExampleSettings Normalize() => this with
    {
        FrequencyHz = Math.Clamp(FrequencyHz, 100_000L, 2_500_000_000L),
        MaximumHistory = Math.Clamp(MaximumHistory, 100, 100_000)
    };
}
```

設定ストアはプラグインIDで分離され、データディレクトリは通常次になります。

```text
%LOCALAPPDATA%\SRdeck\plugins\example.decoder
```

旧版にないフィールド、壊れた値、範囲外入力を想定します。受信中の設定変更は、適用、拒否、再同調、DSPリセットのどれになるかを明確にしてください。

## 9. UIと任意能力を追加する

WPF UIを追加する場合は`IPluginViewProvider`を実装し、Descriptorへ`MainView`／`SettingsView`を追加します。結果や診断も同じ規則です。

| 機能 | インターフェース | 能力フラグ |
|---|---|---|
| メイン／設定ビュー | `IPluginViewProvider` | `MainView`／`SettingsView` |
| プロファイル | `IPluginProfileProvider` | 対応するProfile能力 |
| ライブ変更 | `ILivePluginProfileProvider` | 対応するLive能力 |
| 周波数マーカー | `IFrequencyOverlayProvider` | `FrequencyOverlay` |
| 結果通知 | `IPluginResultProvider` | `ResultPublisher` |
| エクスポート | `IPluginExportProvider` | `Export` |
| 診断 | `IPluginProcessingDiagnosticsProvider` | 対応するDiagnostics能力 |

正確な能力名は現在のContractsを正本とします。UIは幅700 pxへ適応し、DSPからDispatcher経由で一定間隔に更新します。詳細は[右ペインUI設計](Right-Pane-UI-Design)を参照してください。

## 10. 最小テストを書く

最低限、次を自動テストします。

- DescriptorのID、API範囲、Capabilities
- publicな引数なしコンストラクター
- Initialize、Activate、Start、Consume、Stop、Deactivate、Dispose
- Stop、Deactivate、Disposeの二重呼び出し
- キャンセルされたStart／Consume
- RATE、中心周波数、ストリーム世代、不連続でリセット
- leaseをコールバック後に保持しない
- 設定の既定値と範囲外正規化
- 例外時の音声、Timer、Task、ファイル、ネイティブ資源解放

既知IQを使う場合は、サンプルレート、中心周波数、信号振幅、期待フレームをテストデータと一緒に記録します。

## 11. ホストで確認する

クリーンなホストフォルダーで次を確認します。

1. DLLを置く前はMODEに存在しない。
2. DLLと専用依存を置き、再起動すると表示される。
3. Initialize／Activateが1回だけ成功する。
4. Start／Stopを繰り返せる。
5. MODEを別プラグインへ切り替えられる。
6. SDR再検出とRATE変更後に再Startできる。
7. 終了時にプロセスが残らない。

デバッグビルドだけでなく、最終的なRelease配布物でも繰り返します。

## よくある実装ミス

- DLLを`plugins`サブフォルダーへ置く
- Descriptor IDを表示名やローカライズ文字列から生成する
- Capabilitiesと実装インターフェースが一致しない
- `ConsumeAsync`後もleaseのMemoryを保持する
- 周波数変更でDSP状態をリセットしない
- IQコールバックからObservableCollectionを更新する
- StopでバックグラウンドTaskを待たない
- ホスト共有DLLの別版をプラグインへ同梱する
- 開発出力フォルダーの偶然の依存でしか起動確認しない

## 次に読むもの

- 標準チャネルIQ: `docs/samples/SRdeckPlugin.ChannelExample`
- UI: [右ペインUI設計](Right-Pane-UI-Design)
- テストとZIP: [テストと配布](Testing-and-Distribution)
- API全体: [プラグインAPI仕様](https://github.com/sake846/SRdeck/blob/main/docs/plugin-interface-specification.md)
