@echo off
rem Lance build.ps1 depuis cmd, quel que soit le dossier courant (sans changer la strategie d execution).
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
if errorlevel 1 pause
