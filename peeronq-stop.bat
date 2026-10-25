@echo off
REM Stops the scoped web process and Phase 6 containers while preserving their data volumes.
cd /d "%~dp0"
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\windows\peeronq-workspace-dev.ps1" -Action stop
exit /b %errorlevel%
