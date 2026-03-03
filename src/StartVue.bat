@echo off
cd /d "%~dp0"

echo 正在启动 Vue Frontend...
start "Industrial Vue Frontend" cmd /k "cd Industrial.Vue && npm install && npm run dev -- --port 5175 --open"

echo 所有服务启动指令已发送完毕！
