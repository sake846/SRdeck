# SRdeck

SRdeckはSRdeck SDRスイートのホストアプリケーションと、公開プラグイン基盤です。
このリポジトリにはバージョン付きのリリーススナップショットを収録しています。

製品の変更をこのリポジトリへ直接コミットしないでください。通常の開発・レビュー手順を利用してください。

## リリースメタデータ

- リリースバージョン: `1.0.0`

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

- `SRdeck-1.0.0-win-x64-host-only.zip` — オプションプラグインを含まないホストアプリケーション
- `SRdeck-1.0.0-win-x64-with-plugins.zip` — 公開対象プラグインを同梱したホストアプリケーション

パッケージには`SRdeck.exe`、権利・セキュリティ文書、依存関係の通知、
`PACKAGE-MANIFEST.json`を含みます。実行前に.NET 10 Desktop Runtime (x64)を
インストールしてください。埋め込みWebコンテンツを使う機能にはWindows WebView2 Runtimeも必要です。
