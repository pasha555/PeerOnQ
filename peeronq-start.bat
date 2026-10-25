@echo off
setlocal
REM Starts the complete PeerOnQ dev workspace: web preview plus the real Phase 6 stack.
cd /d "%~dp0"
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\windows\peeronq-workspace-dev.ps1" -Action start
set "PEERONQ_START_EXIT_CODE=%errorlevel%"
if not "%PEERONQ_START_EXIT_CODE%"=="0" (
    echo.
    echo PeerOnQ could not start every service. Review the error above or run peeronq-status.bat.
    pause
)
exit /b %PEERONQ_START_EXIT_CODE%
