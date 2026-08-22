# テストと配布

プラグインのテストは、DSPの既知ベクトルだけでなく、ホスト契約、ストリーム不連続、停止、UI、配布フォルダーまで含めます。同じプロセスで動くため、プラグインの停止不能や未処理例外はホスト全体へ影響します。

## テストの層

| 層 | 主な対象 | 例 |
|---|---|---|
| 単体 | DSP、CRC／FEC、Parser、設定正規化 | 既知ベクトル、境界値、破損フレーム |
| ライフサイクル | Module状態遷移と冪等性 | Start失敗、Stop二重、Dispose二重 |
| ストリーム | IQ配送、継続性、コピー、ドロップ | RATE変更、中心変更、不連続 |
| ホスト統合 | 検出、同調、音声、履歴、エクスポート | クリーンホストでMODE切替 |
| UI | 700 px、テーマ、操作、Dispatcher | 高DPI、空状態、最大値 |
| 性能 | リアルタイム余裕、メモリ、停止時間 | 最大RATE／チャネル、長時間実行 |
| 配布 | ZIP内容、依存、版、ライセンス | 新規フォルダーへ展開して起動 |

## スターターの適合テスト

生IQサンプル:

```powershell
dotnet build docs\samples\SRdeckPlugin.Example\SRdeckPlugin.Example.csproj -c Release
dotnet run --project docs\samples\SRdeckPlugin.Example.Tests\SRdeckPlugin.Example.Tests.csproj -c Release
```

標準チャネルIQを使う場合は`docs/samples/SRdeckPlugin.ChannelExample`とそのテストを基準にします。サンプルのテストを複製するだけでなく、自プラグイン固有の失敗経路を追加してください。

## 全体回帰試験

試験名を確認:

```powershell
dotnet run --project SRdeck.Tests\SRdeck.Tests.csproj -- --list-tests
```

全試験:

```powershell
dotnet run --project SRdeck.Tests\SRdeck.Tests.csproj
```

`SRdeck.Tests`はコンソール型ハーネスです。失敗後も後続項目を実行します。終了コードだけでなく、失敗項目名と最初の原因を確認してください。

## Descriptorと検出

最低限確認します。

- IDが構文に一致し、他プラグインと重複しない
- DisplayName、Provider、Licenseが空でない
- Host API 1.0を対象にするなら最小／最大範囲に1.0を含む
- Capabilitiesと実装インターフェースが一致
- publicな引数なしコンストラクター
- アセンブリ名とDLL名が`SRdeckPlugin.*`
- 依存不足時にホスト全体を停止させない

## ライフサイクル

次の正常系と異常系を試します。

```text
Initialize -> Activate -> Start -> Consume -> Stop -> Deactivate -> Dispose
```

- Start前のConsumeを安全に無視／拒否
- Stopを2回
- Deactivateを2回
- Disposeを2回
- Start途中でキャンセル
- Start途中で例外
- Stop中にUI操作
- MODEを素早く切り替え
- Start／Stopを多数回反復
- Dispose後の公開操作を拒否

各経路で音声、Timer、Task、Channel、ネイティブハンドル、ファイルWriterが残らないことを確認します。

## IQと継続性

- leaseをコールバック外へ保持していない
- 非同期キューへ渡す前に所有コピーしている
- キューが有界である
- RATE変更でフィルターと同期器をリセット
- 中心周波数変更でフレーム再構成をリセット
- ストリーム世代変更とドロップを検出
- 空ブロック、最小ブロック、端数ブロック
- キャンセル時にpoolバッファを返す
- 無効なメタデータを安全に扱う

既知IQベクトルは、中心周波数、サンプルレート、振幅、期待結果をテストデータに明記します。

## DSPとプロトコル

- 正常フレーム
- 1-bit／複数bit破損
- CRC／FCS／FEC境界
- 最短／最長フレーム
- 途中で切れたフレーム
- 連結フレーム
- ノイズだけ
- 強すぎる／弱すぎる信号
- 周波数、位相、タイミングずれ
- 対応外メッセージ
- 悪意ある長さ／入れ子／文字列

ParserへRF入力由来の長さや値をそのまま信頼させないでください。

## 設定、履歴、エクスポート

- 設定なしで既定値
- 旧版設定の欠落フィールド
- 範囲外を`Normalize`
- 壊れたJSONから安全に復旧
- プラグイン名前空間へ保存
- 最大履歴件数で切り詰め
- 書き込み不可、ディスクフル、ファイルロック
- CSVの区切り、改行、引用符、文字コード
- JSONのnull、時刻、数値精度
- キャンセルされたエクスポート
- 出力0件の結果

履歴I/Oの失敗でリアルタイムDSPを止めない設計を確認します。

## UI

- 幅700 px、100／125／150%スケール
- 日本語／英語、長いラベル
- 空状態、1件、最大件数
- 検索、ソート、選択詳細
- キーボード操作とフォーカス
- 色だけに依存しない状態
- Dispatcher違反がない
- 非表示タブで過剰更新しない
- View再生成とMODE切替
- エクスポート／キャプチャの二重実行防止

スクリーンショットだけでなく、受信中にUI操作して`PRC`と`WPF`の変化も確認します。

## 性能と耐久

実利用の最大RATE、最大チャネル数、代表PCで測定します。

- Consume処理時間の平均と上位パーセンタイル
- キュー深さとドロップ
- GC割り当てと長時間メモリ
- UI更新頻度
- Stop完了時間
- ファイルとハンドル数
- CPU／GPUフォールバック
- 30分以上の連続受信
- 信号なしと高トラフィックの両方

「平均では間に合う」だけでなく、バースト時にも有界キューが回復することを確認します。

## 配布ZIP

利用者が`SRdeck.exe`と同じフォルダーへ展開できる平坦な構成にします。

```text
SRdeckPlugin.MyDecoder.dll
MyDecoder.NativeDependency.dll
README.md
THIRD-PARTY-NOTICES.md
```

原則として同梱しない共有DLL:

- `SRdeckPlugin.Contracts.dll`
- `SRdeckPlugin.Sdk.dll`
- `SRdeckPlugin.Wpf.dll`
- `SRdeckCore.SignalProcessing.dll`

これらはホストと同じ版を使います。プラグイン固有依存と共有依存を区別し、同名DLL競合を確認してください。

## READMEに必要な情報

- プラグインID、表示名、バージョン
- 対応SRdeckリリース／Host API範囲
- Windows x64／.NET要件
- 対応方式、プロファイル、周波数
- 必要RATEと代表的負荷
- インストール先
- 設定／履歴／キャプチャ保存先
- ネイティブ依存と追加ランタイム
- エクスポート形式
- 既知の制限
- ライセンス、第三者通知、特許通知
- セキュリティ連絡先

## 公開前チェック

1. Releaseビルドをクリーン環境で成功させます。
2. 単体、適合、全体回帰試験を実行します。
3. 配布対象だけを新しいホストフォルダーへ展開します。
4. 検出、受信開始、停止、MODE切替、終了を反復します。
5. 対応するホストとプラットフォームパッケージ版を照合します。
6. ZIPから不要なPDB、キャッシュ、秘密情報、テストデータを除きます。
7. README、LICENSE、第三者通知、既知の制限を確認します。
8. ZIP内容のハッシュ／マニフェストを生成し、公開物と照合します。
9. 別PCまたはクリーンユーザー環境で最終確認します。

詳細な規範は[開発・配布ガイド](https://github.com/sake846/SRdeck/blob/main/docs/plugin-development-guide.md)と[回帰試験仕様](https://github.com/sake846/SRdeck/blob/main/docs/regression-test-specification.md)を参照してください。
