@echo off
setlocal DisableDelayedExpansion
set "JP_ROOT=%~dp0"
if not "%JP_ROOT:~101,1%"=="" (
 echo LK/Vexa needs a shorter folder path. Extract the complete package to C:\LKVS.
 if "%~1"=="" pause
 exit /b 2
)
"%SystemRoot%\System32\chcp.com" 65001 >nul 2>nul
if not exist "%~dp0bin\JPBroker.exe" (
 echo JP300 could not find bin\JPBroker.exe. Extract the complete package before launching.
 if "%~1"=="" pause
 exit /b 2
)
if not exist "%~dp0runtime\webview2-fixed\msedge.dll" if exist "%~dp0scripts\Repository-Runtime.json" (
 "%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -NonInteractive -Command "$ErrorActionPreference='Stop'; & ([scriptblock]::Create([IO.File]::ReadAllText((Join-Path $env:JP_ROOT 'scripts\Restore-RepositoryRuntime.ps1')))) -SourceDirectory $env:JP_ROOT"
 if errorlevel 1 (
  echo LK/Vexa could not prepare the bundled browser. Extract the complete repository ZIP and try again.
  if "%~1"=="" pause
  exit /b 2
 )
)
"%~dp0bin\JPBroker.exe" --boot %*
set "JP_EXIT=%ERRORLEVEL%"
if not "%JP_EXIT%"=="0" if "%~1"=="" pause
exit /b %JP_EXIT%
