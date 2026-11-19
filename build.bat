@echo off
setlocal enabledelayedexpansion
chcp 65001 >nul
cd /d "%~dp0"

echo ============================================
echo   iTunes.SMTC artwork-fix build
echo   Framework-dependent, single file
echo ============================================
echo.

rem Two layouts are in use: this working copy keeps the project under src\,
rem the clean release checkout keeps it at the repository root.
set "PROJ=src\iTunes.SMTC\iTunes.SMTC.csproj"
if not exist "%PROJ%" set "PROJ=iTunes.SMTC\iTunes.SMTC.csproj"
set "LOG=%~dp0build.log"
set "TRIES=2"

if not exist "%PROJ%" (
    echo [ERROR] iTunes.SMTC.csproj not found.
    echo         Run this script from the repository root.
    goto :end
)

set "DOTNET="
where dotnet >nul 2>nul
if not errorlevel 1 set "DOTNET=dotnet"
if not defined DOTNET if exist "C:\Program Files\dotnet\dotnet.exe" set "DOTNET=C:\Program Files\dotnet\dotnet.exe"
if not defined DOTNET (
    echo [ERROR] dotnet not found.
    echo         Install the .NET 8 SDK first:
    echo         https://dotnet.microsoft.com/download/dotnet/8.0
    goto :end
)

set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1
set DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

echo dotnet: !DOTNET!
"!DOTNET!" --version
echo.

echo Required runtime on the target machine:
dir /b /ad "C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App\8.0.*" >nul 2>nul
if not errorlevel 1 (
    echo    WindowsDesktop 8.x  OK
) else (
    echo    WindowsDesktop 8.x  MISSING - install .NET 8 Desktop Runtime
)
dir /b /ad "C:\Program Files\dotnet\shared\Microsoft.AspNetCore.App\8.0.*" >nul 2>nul
if not errorlevel 1 (
    echo    AspNetCore 8.x      OK
) else (
    echo    AspNetCore 8.x      MISSING - install ASP.NET Core 8 Runtime
)
echo.

if exist "%LOG%" del /f /q "%LOG%" >nul 2>nul
if exist "out" rmdir /s /q "out"
if exist "out" (
    echo [ERROR] Cannot clear the out folder.
    echo         out\iTunes.SMTC.exe is running or otherwise locked.
    echo         Close it, then run this script again:
    echo           taskkill /f /im iTunes.SMTC.exe
    goto :end
)
mkdir "out" >nul 2>nul

rem ---------------------------------------------------------------
rem  Framework-dependent single file. Compression stays OFF:
rem  NETSDK1176 rejects EnableCompressionInSingleFile when no
rem  runtime is bundled. Output is about 32 MB.
rem ---------------------------------------------------------------
echo [1/2] Publishing ...
echo       Framework-dependent single file, no compression.
echo       First run restores NuGet packages and may take a few minutes.
set "N=0"

:try
set /a N+=1
if exist "out\iTunes.SMTC.exe" del /f /q "out\iTunes.SMTC.exe" >nul 2>nul
"!DOTNET!" publish "%PROJ%" -c UnpackagedRelease -r win-x64 --self-contained false -p:Platform=x64 -p:PublishSingleFile=true -p:DebugType=none -p:SatelliteResourceLanguages=en -o "out" >> "%LOG%" 2>&1
if not errorlevel 1 goto :ok
if !N! LSS %TRIES% (
    echo       attempt !N! failed, retrying in a moment ...
    ping -n 4 127.0.0.1 >nul 2>nul
    goto :try
)
goto :fail

:ok
echo.
echo [2/2] Done.
echo.
if not exist "out\iTunes.SMTC.exe" (
    echo [WARN] out\iTunes.SMTC.exe not found. Check build.log
    goto :end
)
set "SZ=0"
for %%F in ("out\iTunes.SMTC.exe") do set "SZ=%%~zF"
set /a MB=!SZ! / 1048576
echo   out\iTunes.SMTC.exe
echo        !SZ! bytes   approx !MB! MB
echo.
if exist "%~dp0deploy.bat" (
    echo Next: run deploy.bat
    echo   It stops the app, keeps a rollback copy, copies the new exe and restarts.
) else (
    echo Next: quit the app from the tray, back up the old exe, then copy this one over it.
)
goto :end

:fail
echo.
echo [FAILED] Compile error. Full log follows.
echo ============================================
type "%LOG%"
echo ============================================

:end
echo.
pause
endlocal
exit /b
