@echo off
setlocal
cd /d "%~dp0"
"%~dp0bin\JPBroker.exe" --vcmd %*
exit /b %ERRORLEVEL%
