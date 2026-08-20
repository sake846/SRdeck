# テストと配布

## 最小適合テスト

公式のヘッドレスサンプルには、ライフサイクル、Descriptor、IQ配送、冪等な停止／破棄を確認するコンソールテストがあります。

```powershell
dotnet build docs\samples\SRdeckPlugin.Example\SRdeckPlugin.Example.csproj -c Release
dotnet run --project docs\samples\SRdeckPlugin.Example.Tests\SRdeckPlugin.Example.Tests.csproj -c Release
```

標準チャネルIQを使う場合は`docs/samples/SRdeckPlugin.ChannelExample`と、そのテストも基準にしてください。

## 回帰試験

リポジトリ全体の試験名を確認するには:

```powershell
dotnet run --project SRdeck.Tests\SRdeck.Tests.csproj -- --list-tests
```

全試験を実行するには:

```powershell
dotnet run --project SRdeck.Tests\SRdeck.Tests.csproj
```

`SRdeck.Tests`はxUnitプロジェクトではなく、失敗後も後続項目を実行して結果を表示するコンソール型回帰ハーネスです。試験数を文書へ固定せず、`--list-tests`の出力を正本にします。

## 最低限確認する項目

- DescriptorのID、API範囲、能力フラグ
- `Initialize -> Activate -> Start -> Stop -> Deactivate -> Dispose`の遷移
- Stop、Deactivate、Disposeの冪等性
- ストリーム変更／不連続時のDSP状態リセット
- キャンセルと例外時の資源解放
- IQ leaseをコールバック外へ保持していないこと
- 設定がプラグイン名前空間へ保存されること
- UIがある場合は700 px右ペイン、テーマ、キーボード操作
- 使用するRATEと最大チャネル数でリアルタイム処理できること

## 配布ZIP

利用者がZIPの内容を`SRdeck.exe`と同じフォルダーへ展開できる構成にします。

```text
SRdeckPlugin.MyDecoder.dll
MyDecoder.NativeDependency.dll
README.md
THIRD-PARTY-NOTICES.md
```

ホスト共有のContracts、SDK、WPF、SignalProcessing DLLを、プラグイン固有の異なる版で同梱しないでください。既定のAssemblyLoadContextと単一ディレクトリを共有するため、同名依存DLLのバージョン競合にも注意が必要です。

READMEには、対応SRdeck／Host APIバージョン、対応プロファイル、必要RATE、ネイティブ依存関係、ライセンス、インストール先、既知の制限を記載します。

## 公開前の確認

- Releaseビルドが警告なく成功する
- サンプル／プラグイン固有テストと全体回帰試験が成功する
- クリーンなSRdeckフォルダーへ配布物だけを展開して検出できる
- 起動、受信開始、停止、MODE切り替え、終了を繰り返してリークや停止不能がない
- 対応するプラットフォームNuGetパッケージとホストのバージョンが一致する

詳細は[開発・配布ガイド](https://github.com/sake846/SRdeck/blob/main/docs/plugin-development-guide.md)と[回帰試験仕様](https://github.com/sake846/SRdeck/blob/main/docs/regression-test-specification.md)を参照してください。
