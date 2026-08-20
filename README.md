# SRdeck

SRdeck is the host application and public plugin platform for the SRdeck SDR
suite.  This repository contains a versioned release snapshot.

Do not commit product changes directly to this repository.  Use the project's
normal development and review process for product changes.

## Release metadata

- Release version: `1.0.0`

## Contents

- `SRdeck` — Windows host application
- `SRdeckPlugin.Contracts` — stable host/plugin contracts
- `SRdeckPlugin.Sdk` — plugin lifecycle and development helpers
- `SRdeckPlugin.Wpf` — shared WPF controls and themes
- `SRdeckCore.SignalProcessing` — modulation-independent DSP components
- `docs` — plugin specifications, guides, and samples

Build the repository with:

```powershell
dotnet build SRdeck.sln -c Release
```

This build also compiles the native DLLs in `SRdeck/native/sr_fft` and
`SRdeck/native/sr_gpu` with CMake and copies them to the application output
directory. Install CMake and the Visual Studio 2022 C++ build tools first, and
make sure CMake is available on `PATH`.

The host source snapshot does not embed or build the optional plugins.  Those
are published separately in the `SRdeckPlugins` repository.

## Executable packages

The matching GitHub Release provides framework-dependent Windows x64 packages:

- `SRdeck-1.0.0-win-x64-host-only.zip` — host application without optional plugins.
- `SRdeck-1.0.0-win-x64-with-plugins.zip` — host application with the published plugin set.

The packages include `SRdeck.exe`, legal/security documents, dependency notices,
and a `PACKAGE-MANIFEST.json`. Install the .NET 10 Desktop Runtime (x64) before
running SRdeck. The Windows WebView2 Runtime is also required for features that
use embedded web content.
