@echo off
rem ---------------------------------------------------------------------------
rem  build-windows.cmd: build a standalone MapleLite for 64-bit Windows 10/11.
rem
rem  Double-click it, or run it from a terminal in the repo folder.
rem  Result:  bin\MapleLite-windows-x64\MapleLite.exe   (runs without .NET installed)
rem           bin\MapleLite-windows-x64.zip             (the same folder, zipped to share)
rem
rem  Needs the .NET 8 SDK (or newer) to build. The script offers to install it with winget.
rem  Add --no-pause to skip the "press a key" prompt at the end (for use from other scripts).
rem ---------------------------------------------------------------------------
setlocal EnableExtensions
title MapleLite build (Windows)
cd /d "%~dp0"

set "RID=win-x64"
set "NAME=MapleLite-windows-x64"
set "OUT=%~dp0bin\%NAME%"
set "ZIP=%~dp0bin\%NAME%.zip"
set "PAUSE_AT_END=1"
if /i "%~1"=="--no-pause" set "PAUSE_AT_END=0"

echo.
echo  MapleLite standalone build for Windows x64
echo  ------------------------------------------
echo.

rem 1. The .NET SDK.
where dotnet >nul 2>nul
if errorlevel 1 goto :no_dotnet
dotnet --list-sdks 2>nul | findstr /r "^[0-9]" >nul
if errorlevel 1 goto :no_dotnet
goto :have_dotnet

:no_dotnet
echo  The .NET SDK isn't installed. It's free, from Microsoft, and only needed to build.
where winget >nul 2>nul
if errorlevel 1 (
    echo  Install the .NET 8 SDK from https://dotnet.microsoft.com/download/dotnet/8.0
    echo  then run this script again.
    goto :fail
)
set "ANSWER="
set /p "ANSWER= Install the .NET 8 SDK now with winget? [y/N] "
if /i not "%ANSWER%"=="y" (
    echo  Install it from https://dotnet.microsoft.com/download/dotnet/8.0 then run this script again.
    goto :fail
)
winget install --id Microsoft.DotNet.SDK.8 --exact --source winget
if errorlevel 1 goto :fail
echo.
echo  .NET is installed. Close this window and run the script again so it can find it.
goto :end

:have_dotnet
rem 2. Publish a self-contained copy (the .NET runtime is included, plugins are copied beside it).
echo  Building... (the first build downloads packages and takes a few minutes)
if exist "%OUT%" rmdir /s /q "%OUT%"
if exist "%ZIP%" del /q "%ZIP%"
call :publish
if errorlevel 1 (
    rem A stuck compiler server can report files that exist as missing. Restart it and try once more.
    echo.
    echo  The build failed; restarting the .NET build servers and trying once more...
    dotnet build-server shutdown >nul 2>nul
    call :publish
    if errorlevel 1 goto :fail
)
if not exist "%OUT%\MapleLite.exe" (
    echo  The build finished but MapleLite.exe is missing from %OUT%
    goto :fail
)

rem 3. Zip it for sharing (tar ships with Windows 10 and 11).
echo.
echo  Zipping...
pushd "%~dp0bin"
tar -a -c -f "%NAME%.zip" "%NAME%"
popd
if not exist "%ZIP%" echo  Couldn't make the zip; the folder is still ready to use.

echo.
echo  Done.
echo    Run:    bin\%NAME%\MapleLite.exe
if exist "%ZIP%" echo    Share:  bin\%NAME%.zip
goto :end

:publish
dotnet publish "src\MapleLite.Host" -f net8.0-windows10.0.19041.0 -r %RID% -c Release --self-contained -o "%OUT%" -nologo -v minimal -p:DebugType=none -p:DebugSymbols=false -p:ContinuousIntegrationBuild=true "-p:ArtifactsPath=%LOCALAPPDATA%\MapleLite-build\standalone"
exit /b %errorlevel%

:fail
echo.
echo  The build didn't finish. See the messages above.
if "%PAUSE_AT_END%"=="1" pause
exit /b 1

:end
echo.
if "%PAUSE_AT_END%"=="1" pause
exit /b 0
