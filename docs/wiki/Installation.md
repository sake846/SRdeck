# インストール

## 1. パッケージを選ぶ

[GitHub Releases](https://github.com/sake846/SRdeck/releases)から、同じバージョンのいずれかをダウンロードします。

- `SRdeck-<version>-win-x64-with-plugins.zip`: 公開対象の公式プラグイン8種を同梱。通常はこちらを推奨します。
- `SRdeck-<version>-win-x64-host-only.zip`: ホストのみ。使うプラグインを自分で追加する場合に使用します。

公開対象は[公式プラグイン一覧](Plugins-Overview)を参照してください。開発中または任意配布のプラグインは`with-plugins`に含まれないことがあります。

## 2. ZIPを展開する

書き込み可能な任意のフォルダーへZIP全体を展開し、`SRdeck.exe`を実行します。ZIP内から直接起動せず、ファイル構成を保ったまま展開してください。

公式ZIPはフレームワーク依存パッケージです。Microsoft公式から.NET 10 Desktop Runtime (x64)を導入してください。Windowsが警告を表示した場合は、ダウンロード元と配布物が正しいことを確認してから実行してください。

## 3. SDRのドライバーを準備する

### SDRplay

SDRplay公式のAPI 3.xとドライバーをインストールします。SDRplay APIは真正なSDRplay製品だけに使用し、最新のベンダー規約に従ってください。SRdeckは`sdrplay_api.dll`を同梱せず、まずSDRplay APIの標準インストール先、次にアプリケーションフォルダーを検索します。

### RTL-SDR

信頼できる配布元からGPL互換の64-bit版`rtlsdr.dll`を入手して配置し、対象のRTL-SDRインターフェースにWinUSBドライバーを設定します。公式ZIPは`rtlsdr.dll`を同梱しません。Zadigを使う場合は、対象インターフェースを十分確認してください。

## 4. 初回起動

1. `Settings`で使用するSDR種別を`Auto`、`SDRplay`、`RTL-SDR`から選びます。
2. `Detect`でデバイスを検出します。
3. `RATE`を選びます。
4. 必要に応じて周波数、AGC、ゲインなどを設定します。
5. `Start`を押します。

## 設定と更新

ユーザー設定は主に`%LOCALAPPDATA%\SRdeck`へ保存されます。アプリケーションを更新するときは新しいZIPを別フォルダーへ展開して置き換えても、通常この設定は維持されます。古い版と新しい版のDLLを混在させないでください。
