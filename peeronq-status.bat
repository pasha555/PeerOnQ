@echo off
REM Reports the web preview, Admin console, APIs, observability and container status.
cd /d "%~dp0"
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\windows\peeronq-workspace-dev.ps1" -Action status
exit /b %errorlevel%
