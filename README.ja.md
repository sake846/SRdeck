# SRdeck

[![Wiki](https://img.shields.io/badge/docs-SRdeck_Wiki-blue.svg)](https://github.com/sake846/SRdeck/wiki)
[![Getting Started](https://img.shields.io/badge/guide-入門ガイド-green.svg)](https://github.com/sake846/SRdeck/wiki/Getting-Started)
[![Plugins](https://img.shields.io/badge/plugins-公式プラグイン一覧-orange.svg)](https://github.com/sake846/SRdeck/wiki/Plugins-Overview)

SRdeckはSRdeck SDRスイートのホストアプリケーションと、公開プラグイン基盤です。
このリポジトリにはバージョン付きのリリーススナップショットを収録しています。

## SRdeckの画面

![SRdeckのメイン画面](docs/images/SRdeck.png)

製品の変更をこのリポジトリへ直接コミットしないでください。通常の開発・レビュー手順を利用してください。

## 📖 ドキュメント・Wiki

操作方法やプラグインの利用手順、トラブルシューティング、開発手順は [SRdeck 公式Wiki](https://github.com/sake846/SRdeck/wiki) にまとめられています。

- 🔰 **導入・チュートリアル**: [システム要件](https://github.com/sake846/SRdeck/wiki/System-Requirements) / [インストール](https://github.com/sake846/SRdeck/wiki/Installation) / [最初の受信](https://github.com/sake846/SRdeck/wiki/Getting-Started)
- 🎛️ **操作・設定**: [画面構成と操作](https://github.com/sake846/SRdeck/wiki/UI-Overview) / [SDR入力設定](https://github.com/sake846/SRdeck/wiki/SDR-Source-Settings) / [スペクトラム・ウォーターフォール](https://github.com/sake846/SRdeck/wiki/Spectrum-and-Waterfall)
- 🔌 **プラグイン**: [公式プラグイン一覧](https://github.com/sake846/SRdeck/wiki/Plugins-Overview) / [プラグイン管理](https://github.com/sake846/SRdeck/wiki/Plugin-Management)
- ❓ **トラブル・FAQ**: [よくある質問 (FAQ)](https://github.com/sake846/SRdeck/wiki/FAQ) / [トラブルシューティング](https://github.com/sake846/SRdeck/wiki/Troubleshooting)
- 💻 **プラグイン開発**: [開発概要](https://github.com/sake846/SRdeck/wiki/Developer-Overview) / [開発環境](https://github.com/sake846/SRdeck/wiki/Development-Environment) / [最初のプラグイン](https://github.com/sake846/SRdeck/wiki/Creating-First-Plugin)

## リリースメタデータ

- リリースバージョン: `1.0.8`

## 内容

- `SRdeck` — Windowsホストアプリケーション
- `SRdeckPlugin.Contracts` — ホストとプラグインの安定契約
- `SRdeckPlugin.Sdk` — プラグインのライフサイクルと開発支援
- `SRdeckPlugin.Wpf` — 共通WPFコントロールとテーマ
- `SRdeckCore.SignalProcessing` — 変調方式に依存しないDSPコンポーネント
- `docs` — プラグイン仕様、ガイド、サンプル

## ビルド

先に次の環境を用意してください。

- Windows x64、.NET 10 SDK（Desktop Runtime だけではビルドできません）
- Visual Studio / Build Tools の「C++ によるデスクトップ開発」ワークロード
  - MSVC x64/x86 ビルドツール（VS 2022: v143、VS 2026: v145）
  - Windows 10 SDK または Windows 11 SDK
  - Windows 用 C++ CMake ツール、または別途インストールした CMake
- CMake **3.21以上（VS 2022）／4.2以上（VS 2026）**。PATH に追加するか、
  `CMAKE_EXE` 環境変数へ `cmake.exe` のフルパスを設定
- 初回の NuGet 復元用のインターネット接続

環境を用意したら、`SRdeck.sln`と同じフォルダーにある`build.bat`をダブルクリックします。
コマンドプロンプトから実行する場合は、ソースのルートで次のコマンドを使います。

```cmd
build.bat
```

BATが`dotnet`コマンドの存在を確認し、依存パッケージの復元とソリューションのRelease x64ビルドを
順に実行します。作業フォルダーにはBAT自身の配置先を使うため、別のフォルダーから起動しても
ビルドできます。終了時は結果を確認できるようキー入力を待ちます。

引数で構成と、終了時のキー入力待ちの省略（自動実行用）を指定できます。

```cmd
build.bat Debug
build.bat Release --no-pause
```

復元またはビルドに失敗すると、その時点で停止し、0以外の終了コードを返します。
PowerShellから実行する場合は、`build.bat`の代わりに`.\build.bat`と指定してください。
生成される実行ファイルは次の場所にあります。

```text
SRdeck\bin\x64\Release\net10.0-windows\win-x64\SRdeck.exe
```

Debug構成では、出力パスの`Release`を`Debug`に置き換えてください。

このビルドでは、`SRdeck/native/sr_fft` と `SRdeck/native/sr_gpu` のnative DLLも
自動的にCMakeで構築し、アプリの出力フォルダーへコピーします。事前にCMakeと
上記の C++ ビルドツールと Windows SDK をインストールしてください。
`EnableRx888=true` の場合は `sr_rx888` もビルドします。
CMake が失敗する場合は [開発環境](docs/wiki/Development-Environment.md) の
ネイティブ環境の確認とエラー別の対処を参照してください。自動ビルドは `-A x64` を渡すため、
Visual Studio ジェネレーターを使用します。

CMakeの場所は既存のプロジェクト設定に従い、対応するVisual Studio同梱版やPATHから選択します。
別のCMakeを明示する場合は、コマンドプロンプトで`CMAKE_EXE`を設定してから同じウィンドウで
BATを実行してください。

```cmd
set "CMAKE_EXE=C:\Program Files\CMake\bin\cmake.exe"
build.bat
```

ホストスナップショットにはオプションのプラグインを組み込んでいません。プラグインは`SRdeckPlugins`リポジトリで提供します。

## 実行ファイルパッケージ

対応するGitHub Releaseには、フレームワーク依存のWindows x64パッケージを2種類添付します。

- `SRdeck-1.0.8-win-x64-host-only.zip` — オプションプラグインを含まないホストアプリケーション
- `SRdeck-1.0.8-win-x64-with-plugins.zip` — 公開対象プラグインを同梱したホストアプリケーション

パッケージには`SRdeck.exe`、権利・セキュリティ文書、依存関係の通知、
`PACKAGE-MANIFEST.json`を含みます。実行前に.NET 10 Desktop Runtime (x64)を
インストールしてください。埋め込みWebコンテンツを使う機能にはWindows WebView2 Runtimeも必要です。
