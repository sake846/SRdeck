# SRdeck Wiki

SRdeckは、Windows x64上でSDRplayまたはRTL-SDRからIQサンプルを取り込み、スペクトラム表示、ウォーターフォール表示、音声復調、デジタル信号解析を行うSDR受信ホストです。無線方式ごとの処理はプラグインとして分離されており、同じ受信・表示基盤上で航空、船舶、弱信号通信、920 MHz帯などを切り替えて観測できます。

このWikiは、配布ZIPを使う利用者と、独自プラグインを作る開発者の両方を対象にしています。画面名や設定値は、特に断りがない限り現在の`main`ブランチと同じ版の公式パッケージを前提とします。

## 目的から選ぶ

| やりたいこと | 最初に読むページ | 続けて読むページ |
|---|---|---|
| 初めて受信する | [インストール](Installation) | [最初の受信](Getting-Started) |
| SDRが認識されない | [SDR入力設定](SDR-Source-Settings) | [トラブルシューティング](Troubleshooting) |
| 画面の見方を知る | [画面構成と操作](UI-Overview) | [スペクトラムとウォーターフォール](Spectrum-and-Waterfall) |
| 受信方式を選ぶ | [公式プラグイン一覧](Plugins-Overview) | 各プラグインの個別ページ |
| 音声やIQを保存する | [音声出力とIQキャプチャ](Audio-and-Recording) | 使用するプラグインの個別ページ |
| プラグインを追加する | [プラグイン管理](Plugin-Management) | [FAQ](FAQ) |
| 独自プラグインを作る | [開発概要](Developer-Overview) | [最初のプラグイン](Creating-First-Plugin) |

## SRdeckの処理の流れ

```text
SDRplay / RTL-SDR
        |
        v
  広帯域IQストリーム
        |
        +--> FFT --> スペクトラム / ウォーターフォール
        |
        +--> ホスト共通チャネル抽出
                    |
                    v
             選択中プラグイン
          復調 --> 解析 --> 表示 / 音声 / 履歴 / エクスポート
```

ホストはデバイス検出、サンプルレート、中心周波数、FFT、チャネル抽出、音声ルーティング、設定保存を担当します。プラグインは必要な周波数と帯域をホストへ要求し、方式固有の復調、プロトコル解析、右ペイン表示を担当します。プラグインが要求した帯域を現在の`RATE`でカバーできない場合、受信開始や設定変更が拒否されることがあります。

## 主な機能

- SDRplay API 3.xおよびRTL-SDRからのリアルタイム受信
- CPUまたはGPU FFTによるスペクトラム／ウォーターフォール表示
- クリック、ドラッグ、ホイール、キーボードによる同調と履歴操作
- プラグインごとの復調、フレーム検証、一覧、地図、診断
- CSV／JSONエクスポートとプラグイン別JSONL履歴
- 音声プラグインからWindows既定出力へのモニター音声
- 対応プラグインによる直前3秒または20秒のIQキャプチャ
- `%LOCALAPPDATA%\SRdeck`へのホスト設定とプラグインデータの分離保存

## 公開パッケージ

GitHub Releasesでは、同じホストを基にした次の2種類を配布します。

| ファイル | 内容 | 向いている用途 |
|---|---|---|
| `SRdeck-<version>-win-x64-with-plugins.zip` | ホストと公開対象の公式プラグイン | 通常の利用、初回導入 |
| `SRdeck-<version>-win-x64-host-only.zip` | ホストのみ | プラグインを個別管理する環境 |

どちらもWindows x64向けのフレームワーク依存パッケージです。.NET 10 Desktop Runtime (x64)が必要です。正確な同梱物とバージョンはZIP内の`PACKAGE-MANIFEST.json`で確認できます。

## 現在の範囲と制限

- ユーザー向けSDR入力はSDRplayとRTL-SDRです。
- HackRF、Airspy、RX-888、一般的なRaw IQファイルは現行の公開UIから選択できません。
- メインUIは`MODE`で主プラグインを1つ選ぶ構成です。複数プラグインをタブで自由に並べる画面ではありません。
- ホスト共通の長時間IQ録音、IQファイル再生、シーク、ループは公開されていません。
- 地図を使う機能はWebView2とネットワーク接続を必要とします。
- 受信内容の利用、保存、第三者への提供は、地域の法令、通信の秘密、サービス規約に従ってください。

## 関連リンク

- [SRdeckソースリポジトリ](https://github.com/sake846/SRdeck)
- [リリース](https://github.com/sake846/SRdeck/releases)
- [プラグイン開発ガイド](https://github.com/sake846/SRdeck/blob/main/docs/plugin-development-guide.md)
- [プラグインAPI仕様](https://github.com/sake846/SRdeck/blob/main/docs/plugin-interface-specification.md)
- [セキュリティポリシー](https://github.com/sake846/SRdeck/blob/main/SECURITY.md)
