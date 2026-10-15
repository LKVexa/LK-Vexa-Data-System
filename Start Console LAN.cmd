@echo off
setlocal
cd /d "%~dp0"
echo This explicit test mode listens on the network using unencrypted HTTP and WebSocket.
echo Use only an isolated trusted test network. Prefer the TLS network launcher.
choice /c YN /n /m "Start unencrypted LAN mode? Y/N "
if errorlevel 2 exit /b 1
"%~dp0bin\JPBroker.exe" --allow-insecure-network %*
set "JP_EXIT=%ERRORLEVEL%"
if not "%JP_EXIT%"=="0" pause
exit /b %JP_EXIT%
