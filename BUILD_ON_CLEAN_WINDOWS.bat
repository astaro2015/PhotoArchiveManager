@echo off
chcp 65001 >nul
setlocal EnableExtensions
cd /d "%~dp0"

if not exist "%~dp0VERSION.txt" (
  echo ERROR: VERSION.txt was not found next to BUILD_ON_CLEAN_WINDOWS.bat.
  pause
  exit /b 1
)
set /p PAM_VERSION=<"%~dp0VERSION.txt"
if "%PAM_VERSION%"=="" (
  echo ERROR: VERSION.txt is empty.
  pause
  exit /b 1
)

title Photo Archive Manager %PAM_VERSION% - Build

echo ============================================================
echo        PHOTO ARCHIVE MANAGER %PAM_VERSION% - CLEAN WINDOWS BUILD
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
  echo Single EXE:  BUILD_OUTPUT\PhotoArchiveManager_%PAM_VERSION%_win-x64\PhotoArchiveManager.exe
  echo ZIP:         BUILD_OUTPUT\PhotoArchiveManager_%PAM_VERSION%_win-x64.zip
)
echo.
pause
exit /b %ERR%
