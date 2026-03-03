@echo off
cd /d "%~dp0"

echo 正在启动 WPF Client (后台进行)...
powershell -WindowStyle Hidden -Command "Start-Process dotnet -ArgumentList 'run' -WorkingDirectory 'Industrial.WpfClient' -WindowStyle Hidden"

echo 所有服务启动指令已发送完毕！
