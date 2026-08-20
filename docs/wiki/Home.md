# SRdeck Wiki

SRdeckは、Windows x64向けのSDR受信・解析ホストです。SDRplayまたはRTL-SDRからIQを取得し、スペクトラム／ウォーターフォール表示と、用途別プラグインによる復調・解析を行います。

## 主な機能

- SDRplay、RTL-SDRからのリアルタイム受信
- GPU対応FFTによるスペクトラム／ウォーターフォール表示
- プラグインごとの復調、デコード、表示、エクスポート
- ホスト共通のチューニング、チャネル抽出、音声ルーティング、設定保存
- Analogプラグインによる直前3秒のIQキャプチャ

現在のメイン画面には、一般的なIQファイルを開いて再生・シークする操作はありません。

## まず読むページ

1. [システム要件](System-Requirements)
2. [インストール](Installation)
3. [はじめに](Getting-Started)
4. [公式プラグイン一覧](Plugins-Overview)

## 公開パッケージ

GitHub Releasesでは次の2種類を配布します。

- `SRdeck-<version>-win-x64-host-only.zip`: ホストのみ
- `SRdeck-<version>-win-x64-with-plugins.zip`: ホストと公開対象プラグイン

どちらもWindows x64向けのフレームワーク依存パッケージです。.NET 10 Desktop Runtime (x64)が必要です。

## 関連リンク

- [SRdeckソースリポジトリ](https://github.com/sake846/SRdeck)
- [リリース](https://github.com/sake846/SRdeck/releases)
- [プラグイン開発ガイド](https://github.com/sake846/SRdeck/blob/main/docs/plugin-development-guide.md)
- [プラグインAPI仕様](https://github.com/sake846/SRdeck/blob/main/docs/plugin-interface-specification.md)
