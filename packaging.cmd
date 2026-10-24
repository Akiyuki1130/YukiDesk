@echo off
rem YukiDesk one-click packaging. Builds 4 setup installers:
rem   YukiDesk-Setup-<arch>-full-<ver>.exe  self-contained (bundles .NET 8 runtime)
rem   YukiDesk-Setup-<arch>-slim-<ver>.exe  framework-dependent (needs .NET 8 Desktop Runtime)
rem for arch = x64 / arm64. Output goes to installers\.
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

for %%A in (x64 arm64) do (
    echo.
    echo ========== [%%A full] clean and publish ==========
    if exist "publish\win-%%A" rmdir /s /q "publish\win-%%A"
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
    if exist "publish\win-%%A-slim" rmdir /s /q "publish\win-%%A-slim"
    dotnet publish -c Release -r win-%%A --self-contained false -p:PublishSingleFile=false -p:PublishTrimmed=false -o "publish\win-%%A-slim"
    if errorlevel 1 (
        echo [ERROR] publish failed for win-%%A slim
        exit /b 1
    )
    >"publish\win-%%A-slim\yukidesk.runtime" echo slim

    echo ========== [%%A slim] Inno Setup ==========
    "%ISCC%" /DARCH=%%A /DFLAVOR=slim installer.iss
    if errorlevel 1 (
        echo [ERROR] installer failed for win-%%A slim
        exit /b 1
    )
)

rem Read the packaged FileVersion. Written to a temp file and read with set /p
rem because nested quotes inside for /f are unreliable in cmd.exe.
powershell -NoProfile -Command "(Get-Item 'publish\win-x64\YukiDesk.exe').VersionInfo.FileVersion | Out-File -Encoding ascii '%TEMP%\yukidesk-ver.tmp'" 2>nul
set /p VER=<"%TEMP%\yukidesk-ver.tmp"
del /f /q "%TEMP%\yukidesk-ver.tmp" 2>nul
if not defined VER (
    echo [ERROR] cannot read version, skip installer cleanup and checksum generation
    exit /b 1
)

rem Installer filenames carry the 4-segment FileVersion (%VER%), but the checksum
rem manifest MUST be named with the 3-segment tag version (%VER3%): the updater
rem matches SHA256SUMS-<tag>.txt first, and builds already in the field (v1.0.0
rem through v1.0.8) match ONLY that exact name, so a 4-segment manifest would
rem silently disable integrity checks for users updating from those versions.
for /f "tokens=1-3 delims=." %%a in ("%VER%") do set "VER3=%%a.%%b.%%c"
if not defined VER3 (
    echo [ERROR] cannot derive tag version from %VER%
    exit /b 1
)

echo ========== Clean stale installers (keep %VER%) ==========
rem Keep only current-version assets so manual upload cannot pick up a stale installer.
powershell -NoProfile -Command "Get-ChildItem 'installers\YukiDesk-Setup-*.exe' | Where-Object { $_.Name -notlike '*-%VER%.exe' } | Remove-Item -Force" 2>nul

rem SHA256SUMS-<tag>.txt: the updater downloads this manifest to verify installer integrity.
set "SUMS=installers\SHA256SUMS-%VER3%.txt"
powershell -NoProfile -Command "$f = Get-ChildItem 'installers\YukiDesk-Setup-*-%VER%.exe'; if (-not $f) { exit 1 }; $f | ForEach-Object { '{0}  {1}' -f (Get-FileHash -Algorithm SHA256 $_.FullName).Hash.ToLower(), $_.Name } | Set-Content -Encoding Ascii '%SUMS%'"
if errorlevel 1 (
    echo [ERROR] no installers found for version %VER%, checksum manifest not written
    exit /b 1
)

echo.
echo Done. Assets for release:
dir /b "installers\YukiDesk-Setup-*.exe"
if exist "%SUMS%" (
    type "%SUMS%"
    echo Upload the SHA256SUMS-%VER3%.txt above together with the 4 installers.
)
endlocal
