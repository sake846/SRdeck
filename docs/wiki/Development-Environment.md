# 開発環境

## 必要なもの

- Windows x64
- .NET 10 SDK
- .NET 10とWPFを扱えるVisual Studio、Rider、またはVS Code
- CMake
- Visual Studio 2022 C++ build tools
- Git

ホストとWPFプラグインは`net10.0-windows`、Contracts、SDK、SignalProcessing、ヘッドレスサンプルは`net10.0`を対象にしています。プラットフォームはx64、RIDは`win-x64`です。

## リポジトリ全体をビルドする

```powershell
git clone https://github.com/sake846/SRdeck.git
cd SRdeck
dotnet restore SRdeck.sln
dotnet build SRdeck.sln -c Release
```

`SRdeck`のビルド前に、`SRdeck/native/sr_fft`（CPU FFT）と
`SRdeck/native/sr_gpu`（GPU FFT・GPU描画）もCMakeで自動ビルドされます。
`cmake`をPATHに追加するか、`CMAKE_EXE`環境変数に`cmake.exe`のパスを指定してください。

回帰試験は次で実行します。

```powershell
dotnet run --project SRdeck.Tests\SRdeck.Tests.csproj
```

## リポジトリ内でプラグインを開発する

`SRdeckPlugin.Contracts`と`SRdeckPlugin.Sdk`を`ProjectReference`で参照します。UIが必要な場合だけ`SRdeckPlugin.Wpf`、共通DSPが必要な場合だけ`SRdeckCore.SignalProcessing`を追加します。

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

公式サンプルは`docs/samples/SRdeckPlugin.Example`と`docs/samples/SRdeckPlugin.ChannelExample`にあります。

## 外部リポジトリで開発する

対応するGitHub Releaseから、ホストと同じバージョンの次のNuGetパッケージを取得し、ローカルフィードとして参照します。

- `SRdeckPlugin.Contracts`
- `SRdeckPlugin.Sdk`
- `SRdeckPlugin.Wpf`
- `SRdeckCore.SignalProcessing`

これらはリリース資産として提供されます。公開NuGetフィード上の無条件な最新版を前提にせず、`SRdeckPlatformVersion`をホストのリリース版へ固定してください。

## デバッグ時の配置

ビルドされた`SRdeckPlugin.<Name>.dll`とプラグイン固有の依存DLLを、起動する`SRdeck.exe`と同じフォルダーへ置きます。`plugins`サブフォルダーはDLL検索先ではありません。変更を読み込むにはホストを再起動します。
