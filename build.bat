@echo off
cd /d "%~dp0"
where dotnet >nul 2>&1 || (echo .NET 8 SDK not found. Install it from https://dotnet.microsoft.com/download/dotnet/8.0 & pause & exit /b 1)
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o out
if errorlevel 1 (echo Build failed. & pause & exit /b 1)
echo.
echo Done: %~dp0out\VirtualScreenViewer.exe
start "" "%~dp0out"
pause
