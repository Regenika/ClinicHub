@echo off
rem Double-click to remove the ClinicHub server from this computer. The database is not touched.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall.ps1"
