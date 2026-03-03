@echo off
cd /d "%~dp0"

:: 启动所有服务
call StartServer.bat
call StartWPF.bat
call StartVue.bat

echo 所有服务启动指令已发送完毕！
