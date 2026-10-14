@echo off
chcp 65001 >nul
rem 管理者として起動（CPU 温度・電力・ファン・電圧・メモリ帯域を表示するため。PawnIO ドライバも必要）
cd /d "%~dp0"
if not exist dist\SysMon.exe call "%~dp0build.bat"
powershell -NoProfile -Command "Start-Process -FilePath '%~dp0dist\SysMon.exe' -Verb RunAs"
