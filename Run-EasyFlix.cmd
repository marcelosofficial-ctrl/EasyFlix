@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Run-EasyFlix.ps1" -Configuration Release
set "exitcode=%ERRORLEVEL%"
if not "%exitcode%"=="0" (
    echo.
    echo EasyFlix launcher exited with code %exitcode%.
    pause
)
exit /b %exitcode%
