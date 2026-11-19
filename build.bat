@echo off
setlocal enabledelayedexpansion
chcp 65001 >nul
cd /d "%~dp0"

echo ============================================
echo   iTunes.SMTC artwork-fix build
echo   Framework-dependent + compressed
echo ============================================
echo.

set "DOTNET="
where dotnet >nul 2>nul
if not errorlevel 1 set "DOTNET=dotnet"
if not defined DOTNET if exist "C:\Program Files\dotnet\dotnet.exe" set "DOTNET=C:\Program Files\dotnet\dotnet.exe"

if not defined DOTNET (
    echo [ERROR] dotnet not found.
    echo         Install .NET 8 SDK first:
    echo         https://dotnet.microsoft.com/download/dotnet/8.0
    goto :end
)

set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1
set DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

echo dotnet: !DOTNET!
"!DOTNET!" --version
echo.

set "LOG=%~dp0build.log"
if exist "%LOG%" del /f /q "%LOG%" >nul 2>nul

echo [1/2] Publishing ...
echo        Framework-dependent single file, compressed.
echo        First run restores NuGet packages and may take a few minutes.
echo.

"!DOTNET!" publish "iTunes.SMTC\iTunes.SMTC.csproj" -c UnpackagedRelease -r win-x64 --self-contained false -p:Platform=x64 -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o "out" > "%LOG%" 2>&1

if errorlevel 1 (
    echo.
    echo [FAILED] Compile error. Full log follows.
    echo ============================================
    type "%LOG%"
    echo ============================================
    goto :end
)

echo [2/2] Done.
echo.
if exist "out\iTunes.SMTC.exe" (
    for %%F in ("out\iTunes.SMTC.exe") do echo        %%~fF
    for %%F in ("out\iTunes.SMTC.exe") do echo        size: %%~zF bytes
) else (
    echo [WARN] out\iTunes.SMTC.exe not found. Check build.log
)

echo.
echo Next steps:
echo   1. Quit iTunes.SMTC from the system tray.
echo   2. Back up your current exe.
echo   3. Copy out\iTunes.SMTC.exe over it.
echo   4. Start it and play an Apple Music streaming track.

:end
echo.
pause
