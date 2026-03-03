@echo off
cd /d "%~dp0"

echo 正在启动 Server...
start "Industrial Server" cmd /k "cd Industrial.Server && dotnet run --launch-profile https"

echo 所有服务启动指令已发送完毕！
