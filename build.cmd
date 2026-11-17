@echo off
rem Thin wrapper: all logic lives in build.ps1 because this path may contain
rem non-ASCII characters, which cmd cannot pass through reliably.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
exit /b %ERRORLEVEL%