# インストール

## 1. 配布物を選ぶ

[GitHub Releases](https://github.com/sake846/SRdeck/releases)から、同じリリースのいずれかをダウンロードします。

| パッケージ | 内容 | 選ぶ目安 |
|---|---|---|
| `SRdeck-<version>-win-x64-with-plugins.zip` | ホストと公開対象の公式プラグイン | 初回利用、通常はこちら |
| `SRdeck-<version>-win-x64-host-only.zip` | ホストのみ | プラグインを個別に選定・管理する場合 |

リリース説明、ZIP名、ZIP内の`PACKAGE-MANIFEST.json`のバージョンが一致することを確認してください。`with-plugins`の内容はリリースによって変わる可能性があるため、正確なDLL一覧はマニフェストを正本とします。

## 2. 必要なランタイムを入れる

Microsoft公式から.NET 10 Desktop Runtime (x64)をインストールします。.NET SDKだけを使う開発環境でない限り、Runtimeの種類は「.NET Desktop Runtime」です。

地図機能を使う場合はMicrosoft Edge WebView2 Runtimeも必要です。SDRに応じて、次のドライバー／APIも先に準備します。

- SDRplay: SDRplay API 3.xと製品ドライバー
- RTL-SDR: WinUSBドライバー、64-bit版`rtlsdr.dll`、必要な`libusb-1.0.dll`

## 3. ZIPを展開する

ZIP内から直接`SRdeck.exe`を実行せず、全ファイルを同じフォルダー構成のまま展開します。例:

```text
C:\Apps\SRdeck\
├── SRdeck.exe
├── SRdeck.dll
├── SRdeckPlugin.Contracts.dll
├── SRdeckPlugin.Sdk.dll
├── SRdeckPlugin.Wpf.dll
├── SRdeckPlugin.Analog.dll       with-pluginsの場合
├── SRdeckPlugin.AdsB.dll         with-pluginsの場合
└── PACKAGE-MANIFEST.json
```

アプリケーションフォルダーには、ホストと同じ版のDLLだけを置いてください。古い版の上へ部分的に上書きすると、残ったプラグインや依存DLLが競合することがあります。

Windowsがダウンロード由来の警告を表示した場合は、配布元、リリース、ファイル名を確認します。組織のセキュリティポリシーに反して警告を回避しないでください。

## 4. SDRを準備する

### SDRplay

SDRplay公式のAPI 3.xを導入します。SRdeckは`sdrplay_api.dll`を同梱せず、次の順で検索します。

1. SDRplay APIの標準インストール先
2. `SRdeck.exe`と同じフォルダー

真正なSDRplay製品と、ベンダーが許可する条件で使用してください。

### RTL-SDR

対応する64-bit版`rtlsdr.dll`と`libusb-1.0.dll`を`SRdeck.exe`と同じフォルダーへ配置します。ZadigなどでWinUSBを設定する場合は、対象デバイスとインターフェースを確認してください。別のインターフェースを書き換えると、元の用途で認識されなくなることがあります。

## 5. 初回起動と動作確認

1. `SRdeck.exe`を起動します。
2. `Settings`を開き、SDR種別を`Auto`、`SDRplay`、`RTL-SDR`から選びます。
3. `Detect`を押し、デバイス名やゲイン項目が更新されることを確認します。
4. `RATE`を2 MS/s程度に設定します。
5. `MODE`で目的のプラグインを選びます。
6. プラグインの周波数／プロファイルを設定し、`Start`を押します。
7. スペクトラム、ウォーターフォール、右ペインが更新されることを確認します。

詳しい初回手順は[最初の受信](Getting-Started)を参照してください。

## 更新

設定は通常`%LOCALAPPDATA%\SRdeck`にあるため、アプリケーションフォルダーを更新しても維持されます。安全に更新する手順は次のとおりです。

1. SRdeckを終了します。
2. 必要なら`%LOCALAPPDATA%\SRdeck`をバックアップします。
3. 新しいZIPを新しい空フォルダーへ展開します。
4. 独自プラグインがある場合は、互換性を確認して新しいフォルダーへ追加します。
5. 新しい`SRdeck.exe`を起動し、`PACKAGE-MANIFEST.json`、`MODE`、受信開始を確認します。
6. 問題がなければ旧アプリケーションフォルダーを保管または削除します。

ホスト、Contracts、SDK、WPF、SignalProcessing、プラグインの版を混在させないことが重要です。

## アンインストールと初期化

アプリ本体の削除は、SRdeckを終了して展開先フォルダーを削除します。利用者設定や履歴は残ります。完全に初期化する場合は、内容を確認してから次のフォルダーも退避または削除します。

```text
%LOCALAPPDATA%\SRdeck
```

このフォルダーにはホスト設定だけでなく、プラグイン履歴、局情報、地図状態、IQキャプチャが含まれる場合があります。削除前のバックアップを推奨します。

起動、ランタイム、DLL、デバイス検出で問題が出た場合は[トラブルシューティング](Troubleshooting)を参照してください。
