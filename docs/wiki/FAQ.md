# FAQ

## 導入と対応環境

### 実行に.NETは必要ですか？

はい。公式Windows x64 ZIPには.NETランタイムを内包していないため、.NET 10 Desktop Runtime (x64)が必要です。ソースからビルドする場合は.NET 10 SDKを使用します。「.NET Runtime」だけでなく、WPFを含む「Desktop Runtime」であることを確認してください。

### Windows以外で動作しますか？

公式ホストはWPFを使うWindows x64アプリケーションです。Contracts、SDK、SignalProcessingなど一部ライブラリは`net10.0`ですが、配布ホストはLinuxやmacOS向けではありません。

### 対応SDRは何ですか？

公開UIで選べる入力はSDRplayとRTL-SDRです。HackRF、Airspy、RX-888、一般的なIQファイル入力は現行のユーザー向け機能として実装されていません。

### GPUは必須ですか？

必須ではありません。`Settings`でGPU FFTを無効にしてCPU FFTを使用できます。高解像度FFTや高RATEでは、GPUを使う方が余裕を得られる場合があります。

### WebView2やインターネット接続は必須ですか？

受信、復調、一覧、診断だけなら必須ではありません。地図タブを使う場合はMicrosoft Edge WebView2 Runtimeと地図タイルへのHTTPS接続が必要です。

## パッケージと更新

### `host-only`と`with-plugins`の違いは？

`host-only`はホストのみ、`with-plugins`はホストと公開対象の公式プラグインを含みます。正確な同梱内容はZIP内の`PACKAGE-MANIFEST.json`で確認してください。

### ZIPの中から直接起動できますか？

推奨しません。すべてのファイルを同じ構成のまま書き込み可能なフォルダーへ展開してから`SRdeck.exe`を実行してください。

### 更新時に設定は消えますか？

通常は消えません。主な設定と履歴はアプリフォルダーではなく`%LOCALAPPDATA%\SRdeck`へ保存されます。ただし、更新前にこのフォルダーをバックアップし、新しいZIPは空のフォルダーへ展開することを推奨します。

### 古い版へ戻せますか？

旧アプリケーションフォルダーを残していれば実行ファイルは戻せますが、新しい版が保存した設定や履歴を古い版が解釈できる保証はありません。戻す可能性がある場合は、更新前に`%LOCALAPPDATA%\SRdeck`も世代別にバックアップしてください。

## SDRと受信

### `Auto`はどちらを選びますか？

SDRplayを先に、次にRTL-SDRを検出します。意図しない機種が選ばれる場合や検出を切り分ける場合は、`Settings`で種別を明示してください。

### RATEは大きいほど良いですか？

いいえ。RATEを上げると広い帯域を取り込めますが、USB、FFT、チャネル抽出、プラグイン処理の負荷が増えます。目的チャネルが収まる最小のRATEが基本です。初回は2 MS/s程度から始めてください。

### SPやWFを変えると受信感度も変わりますか？

変わりません。`SP`はスペクトラムの縦位置、`WF`はウォーターフォールの明るさです。実際の入力レベルはSDRのRFゲイン、感度、アンテナ、AGCで調整します。

### プラグインを選ぶと中心周波数が変わるのはなぜですか？

プラグインが必要なチャネルと帯域をホストへ要求するためです。複数チャネルをRATE内へ収めるため、選択したチャネルそのものと中心周波数が一致しないこともあります。

### スペクトラムは動くのに復号されません

SDRとFFTは動作しています。右ペインの`診断`で、入力、選局、検出、同期、復調、CRC／FECのどこまで進むかを確認してください。周波数、PPM、ゲイン、信号強度、帯域、RATE、アンテナが主な確認点です。

## プラグイン

### プラグインDLLはどこへ入れますか？

`SRdeckPlugin.*.dll`を`SRdeck.exe`と同じフォルダーへ置き、SRdeckを再起動します。`plugins`サブフォルダーはDLL検索先ではありません。

### `%LOCALAPPDATA%\SRdeck\plugins`は何ですか？

プラグインごとの`settings.json`、JSONL履歴、IQキャプチャなどを置くデータフォルダーです。アセンブリのインストール先ではありません。

### 複数プラグインを同時に使えますか？

通常のメインUIは`MODE`で主プラグインを1つ選びます。ホストAPIには追加プラグインの同時アクティブ化機構がありますが、利用者が複数タブを自由に同時選択するUIではありません。

### MeshtasticがMODEにありません

Meshtasticはソース公開対象ですが、公式実行ZIPのバイナリ配布対象外です。利用するには互換する環境でソースからビルドし、地域の周波数・通信・ライセンス条件を確認する必要があります。

### 外部プラグインは安全ですか？

プラグインはホストと同じプロセス、同じユーザー権限で動作し、サンドボックス化されません。信頼できる配布元、対応版、ライセンス、依存DLLを確認してください。

## 音声、履歴、ファイル

### IQファイルを再生できますか？

現行メインUIには、IQファイルを開いて再生、シーク、ループする操作はありません。対応プラグインは受信中の直前3秒または20秒を解析用IQ WAVと診断JSONへ保存できます。

### IQ WAVを音声プレーヤーで聞けますか？

IQ WAVはIとQを左右チャネルに格納した解析用データです。通常の復調音声ではありません。中心周波数やプロファイルは同名の診断JSONで確認してください。

### 履歴はどこに保存されますか？

多くのデジタルプラグインは`%LOCALAPPDATA%\SRdeck\plugins\<plugin-id>`のJSONLへ履歴を保存します。最大件数はプラグイン設定で変更できる場合があります。

### CSV／JSONエクスポートとJSONL履歴の違いは？

JSONLはプラグインが再表示や継続利用のために管理する内部履歴です。CSV／JSONエクスポートは、利用者が選んだ保存先へ対象データを書き出す機能です。内部JSONLを直接編集しないでください。

### 設定はどこに保存されますか？

主なファイルは次です。

```text
%LOCALAPPDATA%\SRdeck\appsettings.json
%LOCALAPPDATA%\SRdeck\hardware.json
%LOCALAPPDATA%\SRdeck\last_state.json
%LOCALAPPDATA%\SRdeck\stations.json
%LOCALAPPDATA%\SRdeck\bandplans.json
%LOCALAPPDATA%\SRdeck\plugins\<plugin-id>\settings.json
```

### 設定を初期化するには？

`Settings`のリセット機能を使うか、SRdeckを終了して`%LOCALAPPDATA%\SRdeck`を別の場所へ退避します。履歴やキャプチャも含まれるため、フォルダー全体をいきなり削除しないでください。

## 開発

### プラグイン開発用パッケージはどこにありますか？

対応するGitHub Releaseに`SRdeckPlugin.Contracts`、`SRdeckPlugin.Sdk`、`SRdeckPlugin.Wpf`、`SRdeckCore.SignalProcessing`のNuGetパッケージが添付されます。ホストと同じリリース版をローカルフィードから参照してください。

### APIの正本はどれですか？

コンパイル上の正本は`SRdeckPlugin.Contracts`の公開型、意味と適合条件は[プラグインAPI仕様](https://github.com/sake846/SRdeck/blob/main/docs/plugin-interface-specification.md)です。コード例だけを見て互換性を判断しないでください。
