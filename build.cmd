@echo off
setlocal
cd /d "%~dp0"

echo [1/3] Building NpsYpmLyrics ...
dotnet build -c Release -nologo -nowarn:MSB3277
if errorlevel 1 (
    echo Build failed. Check the errors above.
    exit /b 1
)

set "TARGET=C:\Program Files\NotchPeninsula\plugins\NpsYpmLyrics"

echo [2/3] Deploying to %TARGET%
if not exist "%TARGET%" mkdir "%TARGET%"
copy /y "bin\Release\NpsYpmLyrics.dll" "%TARGET%\" >nul
copy /y "plugin.json" "%TARGET%\" >nul
if errorlevel 1 (
    echo Copy failed. The target folder may need administrator rights.
    exit /b 1
)

echo [3/3] Done. Restart NotchPeninsula to load the plugin.
endlocal
