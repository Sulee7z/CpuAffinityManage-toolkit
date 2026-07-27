@echo off
rem CPU Affinity Manager one-click build launcher
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
pause
