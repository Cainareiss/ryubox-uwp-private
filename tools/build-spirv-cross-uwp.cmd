@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0..\eng\Build-SpirvCrossUwp.ps1" -Configuration Release
exit /b %errorlevel%
