# FAQ

## 実行に.NET Runtimeは必要ですか？

はい。公式のWindows x64 ZIPを実行するには.NET 10 Desktop Runtime (x64)が必要です。ソースからビルドする場合は.NET 10 SDKが必要です。

## Windows以外で動作しますか？

公式ホストはWPFを使用するWindows x64アプリケーションです。Contracts、SDK、SignalProcessingなど一部ライブラリは`net10.0`を対象にしていますが、配布ホストはクロスプラットフォーム対応ではありません。

## 対応SDRは何ですか？

現在はSDRplayとRTL-SDRです。HackRF、Airspy、一般的なIQファイル入力はユーザー向け機能として実装されていません。

## `host-only`と`with-plugins`の違いは？

`host-only`はSRdeck本体だけ、`with-plugins`は本体と公開対象プラグインを含みます。後者の正確な内容はZIP内の`PACKAGE-MANIFEST.json`で確認できます。

## プラグインはどこへ入れますか？

`SRdeckPlugin.*.dll`を`SRdeck.exe`と同じフォルダーへ置き、SRdeckを再起動します。`plugins`サブフォルダーはDLL検索先ではありません。

## 複数プラグインをタブで同時に使えますか？

メインUIの`MODE`は主プラグインを1つ選択します。ホストAPIには追加プラグインの同時実行機構がありますが、通常の画面操作はタブ式の複数選択ではありません。

## IQファイルを再生できますか？

現在のメインUIには、IQファイルを開いて再生・シークする操作はありません。Analogプラグインは受信中の直前約3秒をIQ WAVと診断JSONへ保存できます。

## プラグイン開発用パッケージはどこにありますか？

対応するGitHub Releaseに、`SRdeckPlugin.Contracts`、`SRdeckPlugin.Sdk`、`SRdeckPlugin.Wpf`、`SRdeckCore.SignalProcessing`のNuGetパッケージが添付されます。公開NuGetフィードから無条件に最新版を取得する方式ではないため、ホストと同じリリース版をローカルフィードから参照してください。

## 設定はどこに保存されますか？

主な保存先は`%LOCALAPPDATA%\SRdeck`です。プラグイン設定／データは`%LOCALAPPDATA%\SRdeck\plugins\<plugin-id>`へ分離されます。
