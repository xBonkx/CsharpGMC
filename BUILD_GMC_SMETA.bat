@echo off
setlocal EnableExtensions DisableDelayedExpansion
title GMC-Smeta - Windows Release build

rem Run from any working directory; do not modify Git or SQL Server.
pushd "%~dp0" >nul 2>&1
if errorlevel 1 (
    echo [ERROR] Cannot open the repository folder.
    pause
    exit /b 1
)

echo ============================================================
echo GMC-Smeta - .NET 10 / WPF / SQL Server
echo Windows Release build
echo ============================================================
echo.
echo This script builds files only. It does not modify Git or SQL.
echo.

if not exist "GmcSmeta.slnx" goto :missing_files
if not exist "tools\launcher\GmcSmeta.Launcher\GmcSmeta.Launcher.csproj" goto :missing_files

echo [1/4] Checking .NET 10 SDK...
where dotnet >nul 2>&1
if errorlevel 1 goto :missing_sdk
dotnet --list-sdks | findstr /B /C:"10." >nul 2>&1
if errorlevel 1 goto :missing_sdk
dotnet --version
if errorlevel 1 goto :missing_sdk
echo.

echo [2/4] Restoring NuGet packages...
dotnet restore "GmcSmeta.slnx" --nologo
if errorlevel 1 goto :restore_failed
dotnet restore "tools\launcher\GmcSmeta.Launcher\GmcSmeta.Launcher.csproj" --nologo
if errorlevel 1 goto :restore_failed
echo.

echo [3/4] Building the entire solution (Release)...
dotnet build "GmcSmeta.slnx" -c Release --no-restore --nologo
if errorlevel 1 goto :build_failed
echo.

echo [4/4] Building the update-and-launch EXE (Release)...
dotnet build "tools\launcher\GmcSmeta.Launcher\GmcSmeta.Launcher.csproj" -c Release --no-restore --nologo
if errorlevel 1 goto :launcher_failed
echo.

if /I "%~1"=="test" (
    echo [EXTRA] Running offline Core unit tests...
    dotnet test "tests\GmcSmeta.Core.Tests\GmcSmeta.Core.Tests.csproj" -c Release --no-build --nologo
    if errorlevel 1 goto :tests_failed
    echo.
)

if /I "%~1"=="desktop" (
    echo [EXTRA] Publishing the desktop update-and-launch EXE...
    echo The installer creates a .bak copy of an existing desktop EXE.
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "tools\install-desktop-launcher.ps1"
    if errorlevel 1 goto :desktop_failed
    echo.
)

echo ============================================================
echo [OK] RELEASE BUILD SUCCEEDED
echo ============================================================
echo Application:
echo   src\GmcSmeta.App\bin\Release\net10.0-windows\GMC-Smeta-Next.exe
echo Launcher build:
echo   tools\launcher\GmcSmeta.Launcher\bin\Release\net10.0-windows\GmcSmeta.Launcher.exe
echo.
echo Optional:
echo   BUILD_GMC_SMETA.bat test      - build and run Core tests
echo   BUILD_GMC_SMETA.bat desktop   - build and refresh desktop launcher
echo.
echo SQL Server connection is configured when the application starts.
set "RESULT=0"
goto :finish

:missing_files
echo [ERROR] Required C# solution or launcher project was not found.
goto :failed

:missing_sdk
echo [ERROR] .NET 10 SDK is missing or dotnet is not on PATH.
echo Install .NET 10 SDK for Windows, then reopen this BAT file.
echo https://dotnet.microsoft.com/download/dotnet/10.0
goto :failed

:restore_failed
echo [ERROR] NuGet restore failed. Check network access and the errors above.
goto :failed

:build_failed
echo [ERROR] Solution compilation failed. Send the full errors shown above.
goto :failed

:launcher_failed
echo [ERROR] Application compiled, but launcher compilation failed.
goto :failed

:tests_failed
echo [ERROR] Compilation succeeded, but Core tests failed.
goto :failed

:desktop_failed
echo [ERROR] Compilation succeeded, but desktop launcher installation failed.
goto :failed

:failed
echo.
echo [FAIL] See the errors above. No SQL database was modified by this script.
set "RESULT=1"

:finish
echo.
echo Press any key to close this window...
popd
pause >nul
exit /b %RESULT%
