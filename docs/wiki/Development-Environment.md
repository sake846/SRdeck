# 開発環境

ここでは、SRdeck本体とプラグインをビルド・テストするための環境を整えます。利用者としての導入手順は[インストール](Installation)、新規プラグインの最短手順は[最初のプラグイン](Creating-First-Plugin)を参照してください。

## 必要なツール

最初に、ビルドするソースとソリューションを確認してください。

| ビルド対象 | 必要な環境 |
|---|---|
| 公開 `SRdeckPlugins` の `SRdeckPlugins.sln` | Windows x64、.NET 10 SDK、対応する版のプラットフォーム NuGet パッケージ4個。公開プラグインのビルドは CMake を呼び出しません |
| 公開 `SRdeck` の `SRdeck.sln` | 上記の OS / SDK と、以下の C++ / Windows SDK / CMake。プラットフォームライブラリは同梱ソースからビルドします |
| 開発用統合リポジトリの `SRdeck.sln` | ホストの要件に加え、AribT98 の C ネイティブ CODEC と AribT61 の Rust / Cargo ツールチェーン |

公開プラグインの復元・ビルドコマンドとパッケージの配置は、取得した `SRdeckPlugins` の
`README.ja.md` を参照してください。ホストもソースからビルドする場合には、以下のネイティブ環境が必要です。

| ツール | 用途 |
|---|---|
| Windows x64 | ホストとWPFプラグインの実行 |
| .NET 10 SDK | restore、build、test、pack |
| Visual Studio／Rider／VS Code（任意） | C#、WPF、デバッグ。CLI ビルドだけなら IDE は不要 |
| CMake 3.21以上（VS 2022）／4.2以上（VS 2026） | ホストのCPU／GPUネイティブ部品、開発用 AribT98 CODEC |
| Visual Studio / Build Tools の C++ ツール | MSVC x64ネイティブビルド。VS 2022 は v143、VS 2026 は v145 |
| Windows 10 / 11 SDK | Windows API ヘッダー、ライブラリ、リソースコンパイラー |
| Rust stable / Cargo（統合リポジトリのみ） | AribT61 CODEC。`x86_64-pc-windows-msvc` ツールチェーン |
| Git（ZIP取得なら不要） | ソース取得と差分管理 |
| インターネット接続 | 初回の NuGet / Cargo 依存関係の復元 |

ホストとWPFプラグインは`net10.0-windows`、Contracts、SDK、SignalProcessing、ヘッドレスサンプルは`net10.0`を対象にします。公式プラットフォームはx64、RIDは`win-x64`です。

### C++ / Windows SDK / CMake のインストール

Visual Studio Installer で Visual Studio または Build Tools の「変更」を開き、
**「C++ によるデスクトップ開発」**ワークロードを選択します。インストールの詳細で次を確認してください。

- MSVC の x64/x86 ビルドツール（VS 2022: v143、VS 2026: v145）
- Windows 10 SDK または Windows 11 SDK
- Windows 用 C++ CMake ツール（別途 CMake をインストールする場合は省略可能）

C# 用のワークロードや VS Code の C/C++ 拡張機能だけでは、MSVC と Windows SDK はそろいません。
[Microsoft のインストール手順](https://learn.microsoft.com/en-us/cpp/build/vscpp-step-0-installation)も参照してください。

`CMakeLists.txt` の最小バージョンは3.20ですが、Windows の Visual Studio ジェネレーターには
別の要件があります。[VS 2022 用は3.21で追加](https://cmake.org/cmake/help/latest/generator/Visual%20Studio%2017%202022.html)、
[VS 2026 用は4.2で追加](https://cmake.org/cmake/help/latest/generator/Visual%20Studio%2018%202026.html)されています。

## ツール確認

インストール後に PowerShell を開き直して確認します。以下は **PowerShell 用**です。

```powershell
dotnet --info
cmake --version
cmake --help
git --version
```

`dotnet --info` で .NET 10 SDK、`cmake --version` で使用する CMake の版、
`cmake --help` の Generators 欄で使用する Visual Studio が表示されることを確認します。
Visual Studio 同梱の CMake が PATH にない場合は、C++ ツールが入ったインストールを探し、次のように指定できます。

```powershell
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
# $vs が空なら、先に C++ ビルドツールを追加してください。
$env:CMAKE_EXE = Join-Path $vs 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
& $env:CMAKE_EXE --version
$env:PATH = "$(Split-Path $env:CMAKE_EXE);$env:PATH"
```

別途インストールした CMake を使う場合は、`CMAKE_EXE` にその `cmake.exe` のフルパスを指定します。
ホストの自動ビルドはこの環境変数を使用します。AribT98 と手動の `cmake` コマンドにも
同じ CMake を使用させるには、上記のように PATH へ追加してください。
統合リポジトリをビルドする場合は `cargo --version` と `rustup show` も確認してください。

### ネイティブ環境だけを先に確認する

ソースのルートで、インストール済みの Visual Studio を指定します。VS 2026 の場合は値を
`Visual Studio 18 2026` に置き換えてください。この指定は後続の `dotnet build` にも適用されます。

```powershell
$env:CMAKE_GENERATOR = 'Visual Studio 17 2022'
cmake -S .\SRdeck\native\sr_fft -B .\artifacts\native-check\sr_fft -A x64
cmake --build .\artifacts\native-check\sr_fft --config Release
```

この例はホストのソースがある場合に実行します。新しい出力フォルダーでコンパイラーと Windows SDK を
確認でき、通常のビルドに残った CMake キャッシュの影響を避けられます。

### CMake エラーの確認

| 最初のエラー | 確認・対処 |
|---|---|
| `cmake` が見つからない／終了コード9009 | CMake をインストールし、PATH または上記の `CMAKE_EXE` を設定 |
| `Could not create named generator`／Visual Studio のインスタンスが見つからない | CMake と VS の版の組み合わせ、C++ ワークロードを確認。`CMAKE_GENERATOR` はインストール済みの VS を指定 |
| `CMAKE_C_COMPILER` / `CMAKE_CXX_COMPILER` が未設定／コンパイラーの確認に失敗 | MSVC x64/x86 ツールと Windows SDK を追加し、最初のコンパイル／リンクエラーを確認 |
| Windows SDK が見つからない／`windows.h`、`rc.exe`、`kernel32.lib` がない | Visual Studio Installer で Windows SDK を追加 |
| `does not support platform specification`（Ninja / NMake など） | 自動ビルドは `-A x64` を渡すため Visual Studio ジェネレーターを使用。`CMAKE_GENERATOR` の設定を確認 |
| generator / platform が以前のものと一致しない | ツール変更前の `native/.../build` を別名へ退避して再構成。ホストの `sr_fft`、`sr_gpu`、使用時の `sr_rx888` と、統合リポジトリの `SRdeckPlugin.AribT98/Native/build` が対象 |

VS の変更や Ninja からの切り替え後は、環境変数だけを変更しても既存キャッシュは更新されません。
原因の調査には、後続の `MSB3073` と終了コードだけでなく、その前にある最初の `CMake Error`、
実行したコマンド、`cmake --version` の結果を使います。

## リポジトリを取得する

```powershell
git clone https://github.com/sake846/SRdeck.git
cd SRdeck
dotnet restore SRdeck.sln
```

作業前に`git status`でブランチとローカル変更を確認します。ホスト、プラットフォームライブラリ、プラグインを別リリースから混ぜないでください。

## リポジトリ全体をビルドする

```powershell
dotnet build SRdeck.sln -c Release
```

`SRdeck`のビルド前に次のネイティブ部品もCMakeで構築されます。

- `SRdeck/native/sr_fft`: CPU FFT
- `SRdeck/native/sr_gpu`: GPU FFTとGPU描画
- `SRdeck/native/sr_rx888`: `EnableRx888=true` の場合のみ

開発用統合リポジトリでは、さらに AribT98 の CODEC を CMake、AribT61 の CODEC を Cargo で
ビルドします。公開 `SRdeck` / `SRdeckPlugins` スナップショットにはこの2プラグインは含まれません。

失敗した場合は、CMake、MSVC x64ツールセット、Windows SDK、出力された最初のネイティブエラーを確認します。後続のC#エラーだけを直そうとしないでください。

## 回帰試験

全試験:

```powershell
dotnet test SRdeck.Tests\SRdeck.Tests.csproj -c Release
```

試験名の一覧:

```powershell
dotnet test SRdeck.Tests\SRdeck.Tests.csproj -c Release --list-tests
```

`SRdeck.Tests`はxUnit.net v3を使用し、各回帰試験を独立した子プロセスで実行します。Test Explorerからも個別に選択でき、失敗後も後続項目を実行します。文書へ試験件数を固定せず、`dotnet test --list-tests`を正本にします。

## リポジトリ内プラグイン

最小のヘッドレスプラグインはContractsとSDKを参照します。

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\SRdeckPlugin.Contracts\SRdeckPlugin.Contracts.csproj" />
    <ProjectReference Include="..\SRdeckPlugin.Sdk\SRdeckPlugin.Sdk.csproj" />
  </ItemGroup>
</Project>
```

必要な場合だけ追加します。

- UI: `SRdeckPlugin.Wpf`、TargetFrameworkを`net10.0-windows`、`UseWPF=true`
- 共通DSP: `SRdeckCore.SignalProcessing`
- ネイティブ部品: x64配置、ライセンス、ロード失敗時処理

公式スターター:

- `docs/samples/SRdeckPlugin.Example`: 生IQのヘッドレス例
- `docs/samples/SRdeckPlugin.ChannelExample`: 標準チャネルIQの例

## 外部リポジトリで開発する

対象ホストと同じGitHub Releaseから次のNuGetパッケージを取得します。

- `SRdeckPlugin.Contracts`
- `SRdeckPlugin.Sdk`
- `SRdeckPlugin.Wpf`
- `SRdeckCore.SignalProcessing`

4パッケージを同じ版に揃え、ローカルフィードへ置きます。公開フィードの無条件な最新版を参照せず、`SRdeckPlatformVersion`を対象ホスト版へ固定します。

```xml
<PropertyGroup>
  <SRdeckPlatformVersion>0.1.0</SRdeckPlatformVersion>
</PropertyGroup>

<ItemGroup>
  <PackageReference Include="SRdeckPlugin.Contracts"
                    Version="$(SRdeckPlatformVersion)" />
  <PackageReference Include="SRdeckPlugin.Sdk"
                    Version="$(SRdeckPlatformVersion)" />
</ItemGroup>
```

実際の版は対象Releaseに合わせてください。復元時はローカルフィードに加え、パッケージの一般依存を取得するために組織で許可されたNuGetソースを設定します。

## サンプルをビルドする

```powershell
dotnet build docs\samples\SRdeckPlugin.Example\SRdeckPlugin.Example.csproj -c Release
dotnet run --project docs\samples\SRdeckPlugin.Example.Tests\SRdeckPlugin.Example.Tests.csproj -c Release
```

チャネル例も同様にビルドとテストを実行します。新規プロジェクトを始める前にサンプルが通ることを確認すると、SDK環境と自作コードの問題を分離できます。

## デバッグ配置

プラグインDLLは、実際に起動する`SRdeck.exe`と同じフォルダーへ置きます。`plugins`サブフォルダーではありません。

```text
<debug-host>\
├── SRdeck.exe
├── SRdeckPlugin.MyDecoder.dll
└── MyDecoder.NativeDependency.dll
```

推奨手順:

1. クリーンなホストフォルダーを用意します。
2. ビルド後イベントまたは手動コピーで対象プラグインだけを配置します。
3. SRdeckを再起動します。
4. MODE、Initialize、Activate、Start、Stopをログとデバッガーで確認します。
5. DLLがロックされるため、再ビルド前にSRdeckを終了します。

ホスト本体の出力フォルダーへ多数の開発版を混在させると、依存競合の原因が分かりにくくなります。

## 開発時の確認サイクル

1. `dotnet build`で警告とAPI不一致を確認
2. プラグイン単体テスト
3. ライフサイクル適合テスト
4. クリーンなホストでロード
5. 既知IQ／実信号でDSP確認
6. Start／Stop／MODE切り替え反復
7. Release構成と配布フォルダーで再確認
8. 全体回帰試験

テストと配布の詳細は[テストと配布](Testing-and-Distribution)を参照してください。
