@echo off
call "%~dp0Start-DeviceEdition.cmd" --diagnose
exit /b %errorlevel%
