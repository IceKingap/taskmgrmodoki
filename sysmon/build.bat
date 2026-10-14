@echo off
chcp 65001 >nul
rem SysMon をビルドして dist\SysMon.exe を作る（.NET 10 SDK が必要）
cd /d "%~dp0"
rem 起動中の exe を差し替えたときの残骸を掃除
if exist dist\SysMon.old.exe del /q dist\SysMon.old.exe 2>nul
dotnet publish SysMon.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist
if errorlevel 1 (
  echo ビルドに失敗しました
  pause
  exit /b 1
)
echo.
echo dist\SysMon.exe を作成しました
