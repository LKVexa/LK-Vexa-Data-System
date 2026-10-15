@echo off
setlocal
cd /d "%~dp0"
"%~dp0bin\JPBroker.exe" --verify
if errorlevel 1 exit /b %ERRORLEVEL%
"%~dp0bin\JPBroker.exe" --self-test
if errorlevel 1 exit /b %ERRORLEVEL%
set "TEMP=%~dp0workspace\test-tmp"
set "TMP=%TEMP%"
if not exist "%TEMP%" mkdir "%TEMP%"
cd /d "%~dp0native"
"%~dp0bin\JPTests.exe" -test.v -test.timeout=600s
exit /b %ERRORLEVEL%
