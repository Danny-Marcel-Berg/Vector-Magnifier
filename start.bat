@echo off
setlocal
cd /d "%~dp0"

rem Builds only ZoomHost (the magnifier). The C++ projects in the solution are not used at runtime.

set "DOTNET_EXE=dotnet"
if exist "%USERPROFILE%\.dotnet\dotnet.exe" (
    set "DOTNET_EXE=%USERPROFILE%\.dotnet\dotnet.exe"
)

if /i "%DOTNET_EXE%"=="dotnet" (
    where dotnet >nul 2>&1
    if errorlevel 1 goto :no_dotnet
)

echo Baue ZoomHost (Release ^| x64)...
"%DOTNET_EXE%" build "ZoomHost\ZoomHost.csproj" -c Release -p:Platform=x64 --nologo -v q
if errorlevel 1 (
    echo.
    echo Build fehlgeschlagen! Bitte die Meldungen oben kopieren und schicken.
    pause
    exit /b 1
)

set "APP_EXE=%~dp0ZoomHost\bin\x64\Release\net8.0-windows\win-x64\ZoomHost.exe"
if not exist "%APP_EXE%" (
    echo.
    echo ZoomHost.exe nicht gefunden: "%APP_EXE%"
    pause
    exit /b 1
)

echo.
echo Starte ZoomHost...
echo Hotkeys:
echo   Alt + Plus            : Heranzoomen
echo   Alt + Minus           : Herauszoomen
echo   Strg + Alt + Mausrad  : Zoomen
echo   Alt + 0               : Reset auf 1.0x
echo   Alt + Esc             : Beenden
echo.

start "" "%APP_EXE%"
endlocal
exit /b 0

:no_dotnet
echo.
echo .NET 8 SDK nicht gefunden.
echo Bitte installieren: https://dotnet.microsoft.com/download/dotnet/8.0  (SDK, Windows x64)
pause
exit /b 1
