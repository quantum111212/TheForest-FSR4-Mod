@echo off
setlocal
title The Forest - AMD FSR 4 Mod (1-Click Uninstaller)
echo ============================================================
echo   The Forest - AMD FSR 4 Mod v1.0 (1-Click Uninstaller)
echo ============================================================
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\uninstall.ps1" -SourceDir "%~dp0" -TargetDir "%~1"
set EXITCODE=%ERRORLEVEL%

echo.
pause
exit /b %EXITCODE%
