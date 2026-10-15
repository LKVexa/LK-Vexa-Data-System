@echo off
setlocal
cd /d "%~dp0"
if exist "server-config.json" goto configured
if not "%~1"=="" goto configured
"%~dp0bin\JPBroker.exe" --host 127.0.0.1 --port 8877 --public-origin http://127.0.0.1:8877 --allow-insecure-network
goto finished
:configured
"%~dp0bin\JPBroker.exe" %*
:finished
set "JP_EXIT=%ERRORLEVEL%"
if not "%JP_EXIT%"=="0" pause
exit /b %JP_EXIT%
