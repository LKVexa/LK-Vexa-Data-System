@echo off
setlocal
cd /d "%~dp0"
"%~dp0bin\JPBroker.exe" --doctor
set "JP_EXIT=%ERRORLEVEL%"
if not "%JP_EXIT%"=="0" pause
exit /b %JP_EXIT%
