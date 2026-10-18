@echo off
cd /d "%~dp0"

set "DOTNET=dotnet"
if exist "%ProgramFiles%\dotnet\dotnet.exe" set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"

echo 正在编译 HarmonicaPlayer ...
"%DOTNET%" build ".\HarmonicaPlayer.csproj" -c Release -nologo -v q
if errorlevel 1 (
    echo.
    echo 编译失败。若播放器正在运行，请先关闭它再重试；否则请查看上面的错误信息。
    pause
    exit /b 1
)

start "" ".\bin\Release\net8.0-windows\HarmonicaPlayer.exe"
