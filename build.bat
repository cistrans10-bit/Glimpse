@echo off
rem Builds Glimpse into the "dist" folder and starts it. Needs the .NET 8 SDK or newer (https://dot.net).
cd /d "%~dp0"
dotnet publish Glimpse.csproj -c Release -r win-x64 -p:Platform=x64 -o dist
if errorlevel 1 (
  echo.
  echo Build failed. See the messages above.
  pause
  exit /b 1
)
echo.
echo Built: %~dp0dist\Glimpse.exe
start "" "%~dp0dist\Glimpse.exe"
