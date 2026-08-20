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

## 次に読むもの

- 標準チャネルIQを使う場合: `docs/samples/SRdeckPlugin.ChannelExample`
- UIを追加する場合: [右ペインUI設計](Right-Pane-UI-Design)
- API全体: [プラグインAPI仕様](https://github.com/sake846/SRdeck/blob/main/docs/plugin-interface-specification.md)
