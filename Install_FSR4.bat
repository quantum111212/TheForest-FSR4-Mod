@echo off
setlocal
title The Forest - AMD FSR 4 Mod (1-Click Installer)
echo ============================================================
echo   The Forest - AMD FSR 4 Mod v1.0 (1-Click Installer)
echo ============================================================
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\install.ps1" -SourceDir "%~dp0" -TargetDir "%~1"
set EXITCODE=%ERRORLEVEL%

echo.
if %EXITCODE% EQU 0 (
    echo Press any key to close...
) else (
    echo Installation did not complete (error code %EXITCODE%). Press any key to close...
)
pause >nul
exit /b %EXITCODE%
