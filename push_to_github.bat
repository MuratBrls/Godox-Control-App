@echo off
set "PATH=%LOCALAPPDATA%\Programs\MinGit\cmd;%PATH%"
cd /d "%~dp0"
echo ========================================================
echo   Godox EM68X Studio - GitHub'a Push Islemi
echo ========================================================
echo.
git push -u origin main
echo.
pause
