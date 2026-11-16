@echo off
rem Double-click to update ClinicHub (server + site) on the clinic computer. Settings and secrets are kept.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0update.ps1"
