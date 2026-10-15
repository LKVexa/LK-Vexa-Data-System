@echo off
setlocal
cd /d "%~dp0"
if exist "server-config.json" goto configured
echo Configure server-config.json from server-config.example.json and supply your TLS certificate and key first.
pause
exit /b 1
:configured
"%~dp0bin\JPBroker.exe" %*
set "JP_EXIT=%ERRORLEVEL%"
if not "%JP_EXIT%"=="0" pause
exit /b %JP_EXIT%
