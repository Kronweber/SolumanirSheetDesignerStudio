@echo off
cd /d "%~dp0" || exit /b 1
dotnet build -c Debug %*
