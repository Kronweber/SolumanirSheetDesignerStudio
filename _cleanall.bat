@echo off
cd /d "%~dp0" || exit /b 1
if exist bin\ rd /s /q bin
for /d %%a in (app\*) do for /d %%d in (%%a\*) do for %%n in (bin obj) do if exist "%%d\%%n\" rd /s /q "%%d\%%n"
call _restoreProjects.bat
