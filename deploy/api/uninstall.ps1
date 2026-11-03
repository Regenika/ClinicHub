# Удаление сервера ClinicHub.Api с компьютера клиники (#48). Запуск: двойной клик по uninstall.cmd.
# База данных и её резервные копии не удаляются: они на компьютере с SQL Server.

param(
    [string]$InstallDir = "C:\ClinicHub\Api"
)

$ErrorActionPreference = "Stop"
$ServiceName = "ClinicHub API"
$WebServiceName = "ClinicHub Web"
$UpdateTask = "ClinicHub Web Auto-Update"
$EventSource = "ClinicHub.Api"

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Start-Process powershell.exe -Verb RunAs -ArgumentList @(
        "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$PSCommandPath`"", "-InstallDir", "`"$InstallDir`"")
    exit
}

Write-Host "Удаление сервера рассылок (ClinicHub.Api)" -ForegroundColor Yellow

# Автообновление общее с сайтом и обновляет только то, что установлено. Если сайт остаётся — задачу не трогаем
# (сервер оно больше ставить не будет: его службы нет). Если сайта нет — выключаем и останавливаем идущий запуск.
if (-not (Get-Service -Name $WebServiceName -ErrorAction SilentlyContinue) -and
    (Get-ScheduledTask -TaskName $UpdateTask -ErrorAction SilentlyContinue)) {
    Stop-ScheduledTask -TaskName $UpdateTask -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $UpdateTask -Confirm:$false
    Write-Host "Автообновление выключено (папку C:\ClinicHub\Updater можно удалить)."
}

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    & sc.exe delete $ServiceName | Out-Null
    Write-Host "Служба «$ServiceName» удалена."
}
Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -and $_.CommandLine -like "*$InstallDir*" -and $_.ProcessId -ne $PID } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Start-Sleep -Seconds 2

Get-NetFirewallRule -ErrorAction SilentlyContinue |
    Where-Object { $_.DisplayName -like "clinichub.api*" -or $_.DisplayName -in @("ClinicHub API", "ClinicHub discovery") } |
    Remove-NetFirewallRule
Write-Host "Правила брандмауэра удалены."

if (Test-Path $InstallDir) {
    Remove-Item $InstallDir -Recurse -Force
    Write-Host "Папка $InstallDir удалена (вместе с секретами; при новой установке их спросят заново)."
}
$parent = Split-Path $InstallDir -Parent
if ($parent -and (Test-Path $parent) -and -not (Get-ChildItem $parent -Force | Select-Object -First 1)) {
    Remove-Item $parent -Force
}
if ([System.Diagnostics.EventLog]::SourceExists($EventSource)) {
    Remove-EventLog -Source $EventSource
}

Write-Host ""
Write-Host "Сервер удалён. База данных не тронута." -ForegroundColor Green
Read-Host "Нажмите Enter, чтобы закрыть окно"
