@echo off
REM Restarts the complete scoped dev workspace without deleting local Phase 6 data.
cd /d "%~dp0"
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\windows\peeronq-workspace-dev.ps1" -Action restart
exit /b %errorlevel%
