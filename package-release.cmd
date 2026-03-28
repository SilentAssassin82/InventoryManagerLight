@echo off
powershell.exe -ExecutionPolicy Bypass -NoProfile -File "%~dp0package-release.ps1" %*
