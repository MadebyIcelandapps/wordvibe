@echo off
rem Double-click to install SoundSpell. No admin rights needed.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
if errorlevel 1 (echo. & echo Install failed. & pause & exit /b 1)
timeout /t 6 >nul
