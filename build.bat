@echo off
setlocal EnableExtensions DisableDelayedExpansion
rem Usage: build.bat [configuration] [--no-pause]
set "BUILD_EXIT_CODE=1"
set "BUILD_CONFIGURATION=Release"
if not "%~1"=="" set "BUILD_CONFIGURATION=%~1"

pushd "%~dp0"
if errorlevel 1 goto finish

where dotnet >nul 2>&1
if errorlevel 1 (
    echo ERROR: Install the .NET 10 SDK and reopen this script.
    goto end
)

echo Restoring SRdeck...
dotnet restore "SRdeck.sln" ^
    -p:Configuration=%BUILD_CONFIGURATION% ^
    -p:Platform=x64
set "BUILD_EXIT_CODE=%ERRORLEVEL%"
if not "%BUILD_EXIT_CODE%"=="0" goto end

echo Building SRdeck and native DLLs in %BUILD_CONFIGURATION% x64...
dotnet build "SRdeck.sln" ^
    --configuration "%BUILD_CONFIGURATION%" ^
    --no-restore ^
    -p:Platform=x64
set "BUILD_EXIT_CODE=%ERRORLEVEL%"
if not "%BUILD_EXIT_CODE%"=="0" goto end

echo Build succeeded. SRdeck.exe is in SRdeck\bin\x64\%BUILD_CONFIGURATION%\net10.0-windows\win-x64.

:end
if not "%BUILD_EXIT_CODE%"=="0" (
    echo Build failed. Exit code: %BUILD_EXIT_CODE%
    echo See README.ja.md or README.md for build prerequisites and troubleshooting.
)
popd

:finish
if /i not "%~2"=="--no-pause" pause
exit /b %BUILD_EXIT_CODE%
