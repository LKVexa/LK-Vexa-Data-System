@echo off
setlocal DisableDelayedExpansion
rem Legacy reference only: native 3.0.0 generated-path handling is unchanged.
set "LKVS_REFERENCE_ROOT=%~dp0"
if not "%LKVS_REFERENCE_ROOT:~1,2%"==":\" goto badroot
set "LKVS_REFERENCE_PATH=%LKVS_REFERENCE_ROOT:~0,-1%"
if not "%LKVS_REFERENCE_PATH:~100,1%"=="" goto badroot
for /f "delims=ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 :\._-" %%P in ("%LKVS_REFERENCE_ROOT%") do goto badroot
cd /d "%LKVS_REFERENCE_ROOT%"
if errorlevel 1 goto badroot
rem Direct batch delegation avoids CALL's second expansion.
"%LKVS_REFERENCE_ROOT%Start Console.cmd" %*
exit /b %ERRORLEVEL%
:badroot
echo Extract the full reference bundle to a short writable local folder, preferably C:\LKVS.
echo Use a drive-letter root of at most 100 UTF-16 characters and simple ASCII path characters.
echo UNC paths, network drives and deeply nested roots are unsupported in this legacy bundle.
exit /b 2
