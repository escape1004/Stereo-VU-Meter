@echo off
setlocal
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Manage-Audio-Bridge.ps1"
set "result=%ERRORLEVEL%"
pause
exit /b %result%
