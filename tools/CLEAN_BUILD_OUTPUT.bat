@echo off
chcp 65001 >nul
cd /d "%~dp0\.."
echo Cleaning compiled output while KEEPING any portable Data folders...
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command ^
  "$root = '.\BUILD_OUTPUT'; if (Test-Path $root) { ^
    Get-ChildItem $root -Force ^| Where-Object { -not ($_.PSIsContainer -and ($_.Name -like 'PhotoArchiveManager_*_win-x64' -or $_.Name -eq '_PAM_PORTABLE_DATA_BACKUP')) } ^| Remove-Item -Recurse -Force -ErrorAction SilentlyContinue; ^
    Get-ChildItem $root -Directory -Filter 'PhotoArchiveManager_*_win-x64' -ErrorAction SilentlyContinue ^| ForEach-Object { ^
      Get-ChildItem $_.FullName -Force ^| Where-Object { $_.Name -ne 'Data' } ^| Remove-Item -Recurse -Force -ErrorAction SilentlyContinue ^
    } ^
  }; Get-ChildItem '.\src' -Directory -Recurse ^| Where-Object { $_.Name -in @('bin','obj') } ^| Remove-Item -Recurse -Force -ErrorAction SilentlyContinue"
echo Done. Portable Data folders were kept.
pause
