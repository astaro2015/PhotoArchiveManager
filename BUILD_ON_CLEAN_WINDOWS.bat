@echo off
chcp 65001 >nul
setlocal EnableExtensions
cd /d "%~dp0"
title Photo Archive Manager 1.15.3 - Build

echo ============================================================
echo        PHOTO ARCHIVE MANAGER 1.15.3 - CLEAN WINDOWS BUILD
echo ============================================================
echo.
echo No Visual Studio or preinstalled .NET SDK is required.
echo Internet access is required on the first run.
echo.

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\bootstrap_windows.ps1"
set ERR=%ERRORLEVEL%

echo.
if not "%ERR%"=="0" (
  echo ============================================================
  echo                       BUILD FAILED
  echo ============================================================
  echo See BUILD_OUTPUT\LAST_ERROR.txt and BUILD_OUTPUT\build.log
) else (
  echo ============================================================
  echo                       BUILD SUCCESS
  echo ============================================================
  echo Single EXE:  BUILD_OUTPUT\PhotoArchiveManager_1.15.3_win-x64\PhotoArchiveManager.exe
  echo ZIP:         BUILD_OUTPUT\PhotoArchiveManager_1.15.3_win-x64.zip
)
echo.
pause
exit /b %ERR%
