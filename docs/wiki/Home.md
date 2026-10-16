# SRdeck Wiki

SRdeckはWindows x64向けのSDR受信アプリです。SDRplay、RTL-SDR、HackRF One、RX-888 MK2で受信した信号を表示し、プラグインで音声やデータを復調します。

画面名や設定値は、特に断りがなければ現行の公式パッケージを基準にしています。

![SRdeckのメイン画面](SRdeck.png)

## はじめて使う

次の順に進むと、インストールから受信まで確認できます。

1. [システム要件](System-Requirements)でPCとSDRの準備を確認する
2. [インストール](Installation)でアプリを展開する
3. [最初の受信](Getting-Started)でデバイスを検出し、受信を始める

## 目的から探す

| 知りたいこと | まず読むページ |
|---|---|
| 画面の見方と操作 | [画面構成と操作](UI-Overview) |
| SDRの入力、サンプルレート、ゲイン | [SDR入力設定](SDR-Source-Settings) |
| スペクトラムとウォーターフォール | [スペクトラムとウォーターフォール](Spectrum-and-Waterfall) |
| 受信方式とプラグインの選び方 | [公式プラグイン一覧](Plugins-Overview) |
| プラグインの追加や更新 | [プラグイン管理](Plugin-Management) |
| 音声やIQデータの保存・再生 | [音声出力とIQキャプチャ](Audio-and-Recording) |
| 地図とオフライン表示 | [地図とキャッシュ](Map-and-Cache) |
| エラーや受信できない原因 | [トラブルシューティング](Troubleshooting) / [FAQ](FAQ) |
| プラグインの開発 | [開発概要](Developer-Overview) / [最初のプラグイン](Creating-First-Plugin) |

## 受信の流れ

```text
SDR → 信号の取り込み → スペクトラム／ウォーターフォール
                  └→ チャネル抽出 → プラグイン → 表示／音声／履歴
```

ホストはSDRの検出や信号の取り込み、表示を担当します。プラグインは方式ごとの復調と解析を行います。必要なチャネルが選んだ`RATE`の帯域に収まらない場合、受信開始や周波数の変更ができないことがあります。

## 対応範囲

- 入力にはSDRplay、RTL-SDR、HackRF One、RX-888 MK2を使えます。機種ごとの準備は[システム要件](System-Requirements)を参照してください。
- `MODE`ではメインプラグインを1つ選びます。
- 一部のプラグインではIQキャプチャと保存したIQ WAVの再生ができます。対応状況は[音声出力とIQキャプチャ](Audio-and-Recording)を参照してください。ホスト共通の長時間IQ録音、任意の位置へのシーク、ループ再生には対応していません。
- 地図にはWebView2が必要です。未表示地域の地図を取得するときはインターネット接続が必要ですが、一度表示した範囲はキャッシュからオフラインでも見られます。
- 受信内容の利用、保存、共有は、地域の法令、通信の秘密、サービス規約に従ってください。

## 配布パッケージ

[GitHub Releases](https://github.com/sake846/SRdeck/releases)では、次のZIPを配布しています。

| パッケージ | 内容 | 選ぶ目安 |
|---|---|---|
| `SRdeck-<version>-win-x64-with-plugins.zip` | ホストと公開対象の公式プラグイン | 通常はこちら |
| `SRdeck-<version>-win-x64-host-only.zip` | ホストのみ | プラグインを個別に管理する場合 |

どちらも.NET 10 Desktop Runtime (x64)が必要です。リリースごとの同梱内容はZIP内の`PACKAGE-MANIFEST.json`で確認できます。

## 開発者向け

[開発環境](Development-Environment)を準備し、[最初のプラグイン](Creating-First-Plugin)から作成を始めてください。APIや設計の詳細は[開発概要](Developer-Overview)にまとめています。

## 関連リンク

- [SRdeckソースリポジトリ](https://github.com/sake846/SRdeck)
- [リリース](https://github.com/sake846/SRdeck/releases)
- [プラグイン開発ガイド](https://github.com/sake846/SRdeck/blob/main/docs/contracts/plugin-development-guide.md)
- [プラグインAPI仕様](https://github.com/sake846/SRdeck/blob/main/docs/contracts/plugin-interface-specification.md)
- [セキュリティポリシー](https://github.com/sake846/SRdeck/blob/main/SECURITY.md)
