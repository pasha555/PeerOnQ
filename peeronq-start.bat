@echo off
REM Starts the complete PeerOnQ dev workspace: web preview plus the real Phase 6 stack.
cd /d "%~dp0"
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\windows\peeronq-workspace-dev.ps1" -Action start
exit /b %errorlevel%
