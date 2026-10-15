@echo off
setlocal DisableDelayedExpansion
"%SystemRoot%\System32\chcp.com" 65001 >nul 2>nul
if not exist "%~dp0bin\JPBroker.exe" (
 echo JP300 could not find bin\JPBroker.exe. Extract the complete package before launching.
 if "%~1"=="" pause
 exit /b 2
)
"%~dp0bin\JPBroker.exe" --boot %*
set "JP_EXIT=%ERRORLEVEL%"
if not "%JP_EXIT%"=="0" if "%~1"=="" pause
exit /b %JP_EXIT%
