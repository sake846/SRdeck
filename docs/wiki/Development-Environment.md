# 開発環境

## 必要なツール

| ツール | 用途 |
|---|---|
| Windows x64 | ホストとWPFプラグインの実行 |
| .NET 10 SDK | restore、build、test、pack |
| Visual Studio／Rider／VS Code | C#、WPF、デバッグ |
| CMake | ホストのCPU／GPUネイティブ部品 |
| Visual Studio 2022 C++ Build Tools | MSVC x64ネイティブビルド |
| Git | ソース取得と差分管理 |

ホストとWPFプラグインは`net10.0-windows`、Contracts、SDK、SignalProcessing、ヘッドレスサンプルは`net10.0`を対象にします。公式プラットフォームはx64、RIDは`win-x64`です。

## ツール確認

Developer PowerShellまたは通常のPowerShellで確認します。

```powershell
dotnet --info
cmake --version
git --version
```

`dotnet --info`で.NET 10 SDKとx64環境、`cmake --version`でPATH設定を確認します。CMakeをPATHへ追加しない場合は、`CMAKE_EXE`環境変数へ`cmake.exe`のフルパスを設定できます。

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

失敗した場合は、CMake、MSVC x64ツールセット、Windows SDK、出力された最初のネイティブエラーを確認します。後続のC#エラーだけを直そうとしないでください。

## 回帰試験

全試験:

```powershell
dotnet run --project SRdeck.Tests\SRdeck.Tests.csproj
```

試験名の一覧:

```powershell
dotnet run --project SRdeck.Tests\SRdeck.Tests.csproj -- --list-tests
```

`SRdeck.Tests`はxUnitではなく、失敗後も後続項目を実行して一覧を表示するコンソール型ハーネスです。文書へ試験件数を固定せず、`--list-tests`を正本にします。

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
