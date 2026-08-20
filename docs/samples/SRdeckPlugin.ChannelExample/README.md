# SRdeckPlugin.ChannelExample

ホストの標準チャネル処理を利用する最小のヘッドレスプラグイン例である。
単一プロファイルの同調、同調拒否、チャネル要求、適用構成、リース寿命を示す。

## ビルド

```powershell
dotnet build docs\samples\SRdeckPlugin.ChannelExample\SRdeckPlugin.ChannelExample.csproj -c Release
```

生成DLLと、同じ版のContracts／SDK DLLをSRdeck実行ファイルと同じディレクトリへ配置する。
例の周波数、帯域、レートは動作説明用であり、実際のプラグインでは公開規格と試験結果に基づいて決定する。

この例は生IQフォールバックを許可しない。互換経路が必要な場合だけ `IIqBlockConsumer` も実装し、
`AllowRawIqFallback` を有効にする。標準チャネルと生IQの二重処理を行わない。

