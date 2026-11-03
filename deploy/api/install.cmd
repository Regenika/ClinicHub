@echo off
rem Double-click to install the ClinicHub server. Optional: install.cmd 5113  (another port; by default 5112 or the port of the installed server)
if "%1"=="" (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
) else (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" -Port %1
)
