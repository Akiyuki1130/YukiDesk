@echo off
rem YukiDesk one-click packaging. Builds 4 setup installers:
rem   YukiDesk-Setup-<arch>-full-<ver>.exe  self-contained (bundles .NET 8 runtime)
rem   YukiDesk-Setup-<arch>-slim-<ver>.exe  framework-dependent (needs .NET 8 Desktop Runtime)
rem Usage: packaging.cmd [x64|arm64]. With no argument, builds both architectures.
rem Requirements: .NET 8 SDK and Inno Setup 6 (winget install JRSoftware.InnoSetup).
rem NOTE: keep this file ASCII-only and CRLF (cmd.exe misparses LF-only batch files).
setlocal
cd /d "%~dp0"

rem Locate ISCC.exe: winget per-user install first, then system-wide paths.
set "ISCC=%LocalAppData%\Programs\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" (
    echo [ERROR] Inno Setup 6 not found. Install it with:
    echo     winget install JRSoftware.InnoSetup
    exit /b 1
)

set "YUKI_ARCHES=x64 arm64"
if not "%~2"=="" goto :usage
if /i "%~1"=="x64" set "YUKI_ARCHES=x64"
if /i "%~1"=="arm64" set "YUKI_ARCHES=arm64"
if not "%~1"=="" if "%YUKI_ARCHES%"=="x64 arm64" goto :usage

for %%A in (%YUKI_ARCHES%) do (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0packaging-files.ps1" Clean %%A
    if errorlevel 1 exit /b 1
    echo.
    echo ========== [%%A full] clean and publish ==========
    dotnet publish -c Release -r win-%%A --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -o "publish\win-%%A"
    if errorlevel 1 (
        echo [ERROR] publish failed for win-%%A full
        exit /b 1
    )
    >"publish\win-%%A\yukidesk.runtime" echo full

    echo ========== [%%A full] Inno Setup ==========
    "%ISCC%" /DARCH=%%A /DFLAVOR=full installer.iss
    if errorlevel 1 (
        echo [ERROR] installer failed for win-%%A full
        exit /b 1
    )

    echo ========== [%%A slim] clean and publish ==========
    dotnet publish -c Release -r win-%%A --self-contained false -p:PublishSingleFile=false -p:PublishTrimmed=false -o "publish\win-%%A-slim"
    if errorlevel 1 (
        echo [ERROR] publish failed for win-%%A slim
        exit /b 1
    )
    >"publish\win-%%A-slim\yukidesk.runtime" echo slim

    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0packaging-files.ps1" RuntimeCleanup %%A
    if errorlevel 1 exit /b 1

    echo ========== [%%A slim] Inno Setup ==========
    "%ISCC%" /DARCH=%%A /DFLAVOR=slim installer.iss
    if errorlevel 1 (
        echo [ERROR] installer failed for win-%%A slim
        exit /b 1
    )
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0packaging-files.ps1" Checksums %YUKI_ARCHES%
if errorlevel 1 exit /b 1
exit /b 0

:usage
echo Usage: packaging.cmd [x64^|arm64]
exit /b 1
