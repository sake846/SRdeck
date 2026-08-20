# プラグイン管理

## 同梱プラグイン

`with-plugins`パッケージには、リリース時点の公開対象プラグインが同梱されます。正確な内容はZIP内の`PACKAGE-MANIFEST.json`、概要は[公式プラグイン一覧](Plugins-Overview)で確認できます。

## プラグインDLLの配置

SRdeckは起動時に、`SRdeck.exe`と同じフォルダーだけを非再帰で検索します。検索対象は次の名前に一致するDLLです。

```text
SRdeckPlugin.*.dll
```

例:

```text
SRdeck/
├── SRdeck.exe
├── SRdeckPlugin.Analog.dll
├── SRdeckPlugin.Ais.dll
└── SRdeckPlugin.MyDecoder.dll
```

`plugins`サブフォルダーはDLLの検索先ではありません。`%LOCALAPPDATA%\SRdeck\plugins\<plugin-id>`はプラグインごとの設定／データ保存先であり、アセンブリ配置先とは別です。

## 追加と更新

1. SRdeckを終了します。
2. プラグイン本体と、そのプラグインだけが必要とする依存DLLを`SRdeck.exe`と同じフォルダーへコピーします。
3. SRdeckを起動します。
4. `MODE`の一覧と状態表示を確認します。

ホットリロードには対応していません。更新時は同じプラグインの新旧DLLや依存関係を混在させないでください。

ホストと共有する`SRdeckPlugin.Contracts`、`SRdeckPlugin.Sdk`、`SRdeckPlugin.Wpf`などは、原則としてホスト側の同一バージョンを使用します。プラグインごとに異なる版を同じフォルダーへ置くと競合する可能性があります。

## 信頼境界

プラグインDLLはSRdeckと同じプロセス・同じユーザー権限で実行され、サンドボックス化やホストによる署名検証は行われません。ファイル、デバイス、ネットワークへアクセスできるため、信頼できる配布元のDLLだけを導入してください。公式ZIPでは`PACKAGE-MANIFEST.json`と実際の`SRdeckPlugin.*.dll`を照合し、第三者DLLを追加した場合はそのライセンス、署名、ハッシュ、依存関係を配布元で確認してください。

## 選択と同時実行

メインUIの`MODE`は、右ペインへ表示して動作させる主プラグインを1つ選びます。ホストAPIには追加プラグインを同時アクティブ化する機構がありますが、通常の`MODE`操作で複数タブを同時に有効化するものではありません。

## 読み込まれない場合

- DLL名が`SRdeckPlugin.*.dll`になっているか
- DLLが`SRdeck.exe`と同じ階層にあるか
- 対象がWindows x64、現行ホストAPI、現行.NETターゲットと互換か
- 必要な依存DLLが不足していないか
- プラグインIDやエントリクラスがAPI要件を満たすか

詳細は[トラブルシューティング](Troubleshooting)と[プラグインAPI仕様](https://github.com/sake846/SRdeck/blob/main/docs/plugin-interface-specification.md)を参照してください。
