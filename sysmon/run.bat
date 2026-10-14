@echo off
chcp 65001 >nul
rem SysMon を起動する（未ビルドなら先にビルド）
cd /d "%~dp0"
if not exist dist\SysMon.exe call "%~dp0build.bat"
start "" dist\SysMon.exe
