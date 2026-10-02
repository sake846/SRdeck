# SRdeck

[![Wiki](https://img.shields.io/badge/docs-SRdeck_Wiki-blue.svg)](https://github.com/sake846/SRdeck/wiki)
[![Getting Started](https://img.shields.io/badge/guide-入門ガイド-green.svg)](https://github.com/sake846/SRdeck/wiki/Getting-Started)
[![Plugins](https://img.shields.io/badge/plugins-公式プラグイン一覧-orange.svg)](https://github.com/sake846/SRdeck/wiki/Plugins-Overview)

SRdeckはSRdeck SDRスイートのホストアプリケーションと、公開プラグイン基盤です。
このリポジトリにはバージョン付きのリリーススナップショットを収録しています。

製品の変更をこのリポジトリへ直接コミットしないでください。通常の開発・レビュー手順を利用してください。

## 📖 ドキュメント・Wiki

操作方法やプラグインの利用手順、トラブルシューティング、開発手順は [SRdeck 公式Wiki](https://github.com/sake846/SRdeck/wiki) にまとめられています。

- 🔰 **導入・チュートリアル**: [システム要件](https://github.com/sake846/SRdeck/wiki/System-Requirements) / [インストール](https://github.com/sake846/SRdeck/wiki/Installation) / [最初の受信](https://github.com/sake846/SRdeck/wiki/Getting-Started)
- 🎛️ **操作・設定**: [画面構成と操作](https://github.com/sake846/SRdeck/wiki/UI-Overview) / [SDR入力設定](https://github.com/sake846/SRdeck/wiki/SDR-Source-Settings) / [スペクトラム・ウォーターフォール](https://github.com/sake846/SRdeck/wiki/Spectrum-and-Waterfall)
- 🔌 **プラグイン**: [公式プラグイン一覧](https://github.com/sake846/SRdeck/wiki/Plugins-Overview) / [プラグイン管理](https://github.com/sake846/SRdeck/wiki/Plugin-Management)
- ❓ **トラブル・FAQ**: [よくある質問 (FAQ)](https://github.com/sake846/SRdeck/wiki/FAQ) / [トラブルシューティング](https://github.com/sake846/SRdeck/wiki/Troubleshooting)
- 💻 **プラグイン開発**: [開発概要](https://github.com/sake846/SRdeck/wiki/Developer-Overview) / [開発環境](https://github.com/sake846/SRdeck/wiki/Development-Environment) / [最初のプラグイン](https://github.com/sake846/SRdeck/wiki/Creating-First-Plugin)

## リリースメタデータ

- リリースバージョン: `1.0.6`

## 内容

- `SRdeck` — Windowsホストアプリケーション
- `SRdeckPlugin.Contracts` — ホストとプラグインの安定契約
- `SRdeckPlugin.Sdk` — プラグインのライフサイクルと開発支援
- `SRdeckPlugin.Wpf` — 共通WPFコントロールとテーマ
- `SRdeckCore.SignalProcessing` — 変調方式に依存しないDSPコンポーネント
- `docs` — プラグイン仕様、ガイド、サンプル

## ビルド

```powershell
dotnet build SRdeck.sln -c Release
```

このビルドでは、`SRdeck/native/sr_fft` と `SRdeck/native/sr_gpu` のnative DLLも
自動的にCMakeで構築し、アプリの出力フォルダーへコピーします。事前にCMakeと
Visual Studio 2022のC++ビルドツールをインストールし、CMakeをPATHに追加してください。

ホストスナップショットにはオプションのプラグインを組み込んでいません。プラグインは`SRdeckPlugins`リポジトリで提供します。

## 実行ファイルパッケージ

対応するGitHub Releaseには、フレームワーク依存のWindows x64パッケージを2種類添付します。

- `SRdeck-1.0.6-win-x64-host-only.zip` — オプションプラグインを含まないホストアプリケーション
- `SRdeck-1.0.6-win-x64-with-plugins.zip` — 公開対象プラグインを同梱したホストアプリケーション

パッケージには`SRdeck.exe`、権利・セキュリティ文書、依存関係の通知、
`PACKAGE-MANIFEST.json`を含みます。実行前に.NET 10 Desktop Runtime (x64)を
インストールしてください。埋め込みWebコンテンツを使う機能にはWindows WebView2 Runtimeも必要です。
