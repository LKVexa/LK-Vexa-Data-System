@echo off
setlocal
cd /d "%~dp0"
"%~dp0bin\JPBroker.exe" --restore %*
exit /b %ERRORLEVEL%
