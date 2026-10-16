# 開発環境

公開している [SRdeck](https://github.com/sake846/SRdeck) と [SRdeckPlugins](https://github.com/sake846/SRdeckPlugins) のソースをビルドするための環境を説明します。ソースの取得、プラットフォームパッケージの配置、`build.bat`の使い方は、各リポジトリの[日本語README（SRdeck）](https://github.com/sake846/SRdeck/blob/main/README.ja.md)、[日本語README（SRdeckPlugins）](https://github.com/sake846/SRdeckPlugins/blob/main/README.ja.md)を参照してください。このページでは主にC++／CMake環境の準備とエラーの確認方法を扱います。

## 必要な環境

| ビルド対象 | 必要な環境 |
|---|---|
| 公開 `SRdeck` のホストアプリ | Windows x64、.NET 10 SDK、C++ ビルドツール、Windows SDK、CMake、初回のNuGet復元用インターネット接続 |
| 公開 `SRdeckPlugins` のプラグインのみ | Windows x64、.NET 10 SDK、対応する版のプラットフォームNuGetパッケージ4個、復元用インターネット接続。Gitはソース取得に使う場合のみ必要で、CMakeは不要 |

ホストの実行ファイルパッケージを使う場合は、ビルド環境とは別に.NET 10 Desktop Runtime（x64）とWebView2 Runtimeが必要です。Windows 10 / 11には通常WebView2 Runtimeが標準搭載されています。

プラグインが参照するパッケージは次の4個です。すべて`SRdeckPlugins` READMEに示された同じプラットフォーム版にそろえ、対応するSRdeckリリースのホストで動作確認してください。

- `SRdeckPlugin.Contracts`
- `SRdeckPlugin.Sdk`
- `SRdeckPlugin.Wpf`
- `SRdeckCore.SignalProcessing`

## C++ / Windows SDK / CMake を用意する

Visual Studio InstallerでVisual StudioまたはBuild Toolsの「変更」を開き、**「C++ によるデスクトップ開発」**ワークロードを選択します。インストールの詳細で次を確認してください。

- MSVCのx64/x86ビルドツール（VS 2022: v143、VS 2026: v145）
- Windows 10 SDKまたはWindows 11 SDK
- Windows用C++ CMakeツール。別途CMakeをインストールする場合は省略できます

CMakeはVS 2022では3.21以上、VS 2026では4.2以上を使用してください。CMakeをPATHに追加するか、ホストのビルド時に`CMAKE_EXE`環境変数で`cmake.exe`のフルパスを指定します。

`CMakeLists.txt`の最小バージョンは3.20ですが、Visual Studioジェネレーターには別の要件があります。[VS 2022用ジェネレーター](https://cmake.org/cmake/help/latest/generator/Visual%20Studio%2017%202022.html)はCMake 3.21、[VS 2026用ジェネレーター](https://cmake.org/cmake/help/latest/generator/Visual%20Studio%2018%202026.html)はCMake 4.2で追加されています。

Microsoftの手順は[Visual C++のインストール](https://learn.microsoft.com/en-us/cpp/build/vscpp-step-0-installation)を参照してください。C#用のワークロードやVS CodeのC/C++拡張機能だけでは、MSVCとWindows SDKはそろいません。

## ツールを確認する

インストール後にPowerShellを開き直し、次のコマンドを実行します。

```powershell
dotnet --info
cmake --version
cmake --help
git --version
```

`dotnet --info`に.NET 10 SDKが表示されること、`cmake --version`が必要な版以上であることを確認します。`cmake --help`のGenerators欄には、使うVisual Studioのジェネレーターが表示されます。GitはGitでソースを取得するときに確認してください。

ホストのREADMEと同じコマンドプロンプトで、CMakeの場所を明示する例です。

```cmd
set "CMAKE_EXE=C:\Program Files\CMake\bin\cmake.exe"
build.bat
```

Visual Studio同梱版を指定する場合は、その`cmake.exe`のフルパスを設定します。PowerShellからビルドする場合は`$env:CMAKE_EXE`に同じパスを設定してから`.\build.bat`を実行してください。

## ネイティブ環境を先に確認する

公開 `SRdeck` のソースルートで、ホストのCPU FFTを新しい出力フォルダーへ構成・ビルドできます。VS 2026を使う場合は、ジェネレーターを`Visual Studio 18 2026`に変更してください。

```powershell
$env:CMAKE_GENERATOR = 'Visual Studio 17 2022'
$cmakeExe = if ($env:CMAKE_EXE) { $env:CMAKE_EXE } else { 'cmake' }
& $cmakeExe -S .\SRdeck\native\sr_fft -B .\artifacts\native-check\sr_fft -A x64
& $cmakeExe --build .\artifacts\native-check\sr_fft --config Release
```

新しい出力フォルダーを使うと、既存ビルドのCMakeキャッシュに影響されず、コンパイラーとWindows SDKを確認できます。この手動確認には、PATH上の`cmake`か`CMAKE_EXE`で指定した実行ファイルを使ってください。

## CMakeエラーを確認する

| 最初のエラー | 確認・対処 |
|---|---|
| `cmake`が見つからない／終了コード9009 | 手動実行ではCMakeをPATHに追加するか、フルパスで実行します。ホストの`build.bat`では`CMAKE_EXE`を設定できます |
| `Could not create named generator`／Visual Studioのインスタンスが見つからない | CMakeとVisual Studioの版の組み合わせ、C++ワークロードを確認します。`CMAKE_GENERATOR`はインストール済みのVisual Studioを指定します |
| `CMAKE_C_COMPILER`／`CMAKE_CXX_COMPILER`が未設定、またはコンパイラーの確認に失敗 | MSVC x64/x86ツールとWindows SDKを追加し、最初のコンパイル／リンクエラーを確認します |
| Windows SDKが見つからない／`windows.h`、`rc.exe`、`kernel32.lib`がない | Visual Studio InstallerでWindows SDKを追加します |
| `does not support platform specification`（Ninja / NMakeなど） | ホストの自動ビルドは`-A x64`を渡すため、Visual Studioジェネレーターを使います。`CMAKE_GENERATOR`の設定を確認します |
| generator / platformが以前の設定と一致しない | Visual Studioやジェネレーターを変更した場合は、`SRdeck/native/sr_fft/build`、`sr_gpu/build`、`sr_rx888/build`の既存キャッシュを退避して再構成します |

環境変数を変更しても、既存のCMakeキャッシュは更新されません。後続の`MSB3073`や終了コードだけでなく、その前に出た最初の`CMake Error`、実行したコマンド、`cmake --version`の結果を確認してください。公開 `SRdeckPlugins` のプラグインビルドはCMakeを呼び出さないため、プラグインだけをビルドしている場合は、実行したコマンドと対象ソリューションを確認します。

## SRdeckホストをビルドする

公開 `SRdeck` リポジトリのソースルートで、コマンドプロンプトから`build.bat`を実行します。BATが依存関係を復元し、Release x64で`SRdeck.sln`をビルドします。

```cmd
build.bat
build.bat Debug
build.bat Release --no-pause
```

ホストのビルドでは`SRdeck/native/sr_fft`と`SRdeck/native/sr_gpu`のネイティブDLLもCMakeで構築されます。`EnableRx888=true`の場合は`sr_rx888`も対象です。生成されるホスト実行ファイルは次の場所にあります。

```text
SRdeck\bin\x64\Release\net10.0-windows\win-x64\SRdeck.exe
```

## SRdeckPluginsをビルドする

公開 `SRdeckPlugins` リポジトリの`README.ja.md`に従い、対応するSRdeckリリースのプラットフォームNuGetパッケージ4個を取得して、展開せずに`platform-packages`へ置きます。パッケージの版を対象ホストと一致させてから、同リポジトリの`build.bat`を実行してください。

この公開プラグインスナップショットのビルドはCMakeを呼び出しません。SRdeckホストもソースからビルドする場合は、上記のC++ビルドツール、Windows SDK、CMakeを用意してください。プラグインDLLとプラグイン固有の依存DLLを使って動作確認するときは、対象ホストの`SRdeck.exe`と同じフォルダーへ配置してホストを再起動します。

新しいプラグインの作成手順は[最初のプラグイン](Creating-First-Plugin)、設計の概要は[開発概要](Developer-Overview)を参照してください。
