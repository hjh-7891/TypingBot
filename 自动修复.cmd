@echo off
setlocal
chcp 65001 >nul
cd /d "%~dp0"
title 打字大战 emoji - 自动打字机器人

if not exist "bin\Release\net10.0\TypingBot.exe" (
    echo 首次运行，正在编译...
    dotnet build TypingBot.csproj -c Release --nologo
    if errorlevel 1 goto :failed
)

"bin\Release\net10.0\TypingBot.exe" --cps 60 %*
echo.
echo 机器人已退出。
pause
exit /b 0

:failed
echo.
echo 编译失败，请确认已安装 .NET 10 SDK。
pause
exit /b 1
