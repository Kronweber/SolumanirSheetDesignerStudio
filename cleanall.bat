@echo off
cd /d "%~dp0" || exit /b 1
if exist bin\ rd /s /q bin
for /d %%d in (src\* tests\*) do for %%n in (bin obj) do if exist "%%d\%%n\" rd /s /q "%%d\%%n"
