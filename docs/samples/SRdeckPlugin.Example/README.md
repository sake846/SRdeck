# SRdeckPlugin.Example

生IQを受信する最小のヘッドレスプラグイン例である。設定、UI、同調、結果発行を持たず、
エントリポイント、ライフサイクル、連続性追跡、IQリースの基本だけを示す。

## ビルド

リポジトリルートから実行する。

```powershell
dotnet build docs\samples\SRdeckPlugin.Example\SRdeckPlugin.Example.csproj -c Release
dotnet run --project docs\samples\SRdeckPlugin.Example.Tests\SRdeckPlugin.Example.Tests.csproj -c Release
```

生成された `SRdeckPlugin.Example.dll` を、同じ版の `SRdeckPlugin.Contracts.dll`、
`SRdeckPlugin.Sdk.dll` とともにSRdeckの実行ファイルと同じディレクトリへ配置し、アプリを再起動する。

## この例から追加するもの

- 同調とプロファイル: `IPluginProfileProvider` と `HostContext.Tuning`
- 標準チャネルIQ: `IPluginChannelBlockConsumer`
- 結果通知: `IPluginResultProvider`
- エクスポート: `IPluginExportProvider`
- WPF UI: `net10.0-windows`、`UseWPF=true`、`SRdeckPlugin.Wpf`、`IPluginViewProvider`

能力フラグは、追加した任意契約と一致させる。WPFを参照したまま `Headless` を宣言せず、
ヘッドレスを宣言する場合はビューを生成せず全ライフサイクルを完了できることをテストする。

隣接する `SRdeckPlugin.Example.Tests` は、ホストを起動せずに記述子、ライフサイクル、IQ消費、
反復停止・破棄を確認する最小例である。実際のプラグインでは、これに既知IQ、ブロック境界、
不連続、同調拒否、設定移行、リソース解放の試験を追加する。

