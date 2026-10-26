# Удаление сайта ClinicHub.Web с компьютера клиники. Запуск: двойной клик по uninstall.cmd.
# Файл с контентом site-content.json сохраняется на рабочий стол, чтобы правки не пропали.

param(
    [string]$InstallDir = "C:\ClinicHub\Web"
)

$ErrorActionPreference = "Stop"
$ServiceName = "ClinicHub Web"
$OldTaskName = "ClinicHub Web"
$FirewallRule = "ClinicHub Web"
$EventSource = "ClinicHub.Web"

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Start-Process powershell.exe -Verb RunAs -ArgumentList @(
        "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$PSCommandPath`"", "-InstallDir", "`"$InstallDir`"")
    exit
}

Write-Host "Удаление сайта клиники (ClinicHub.Web)" -ForegroundColor Yellow

# Автообновление (auto-update.ps1) иначе поставило бы сайт обратно.
if (Get-ScheduledTask -TaskName "ClinicHub Web Auto-Update" -ErrorAction SilentlyContinue) {
    # Остановить идущий запуск: иначе начатая сборка доделается и поставит сайт заново уже после удаления.
    Stop-ScheduledTask -TaskName "ClinicHub Web Auto-Update" -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName "ClinicHub Web Auto-Update" -Confirm:$false
    Write-Host "Автообновление выключено (папку C:\ClinicHub\Updater можно удалить)."
}

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    & sc.exe delete $ServiceName | Out-Null
    Write-Host "Служба «$ServiceName» удалена."
}
# Задача планировщика от прошлых версий установщика.
if (Get-ScheduledTask -TaskName $OldTaskName -ErrorAction SilentlyContinue) {
    Stop-ScheduledTask -TaskName $OldTaskName -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $OldTaskName -Confirm:$false
    Write-Host "Старая задача планировщика удалена."
}
Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -and $_.CommandLine -like "*$InstallDir*" -and $_.ProcessId -ne $PID } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Start-Sleep -Seconds 2

Get-NetFirewallRule -DisplayName $FirewallRule -ErrorAction SilentlyContinue | Remove-NetFirewallRule
Write-Host "Правило брандмауэра удалено."

$content = Join-Path $InstallDir "site-content.json"
if (Test-Path $content) {
    $backup = Join-Path ([Environment]::GetFolderPath("Desktop")) "site-content (копия $(Get-Date -Format 'yyyy-MM-dd')).json"
    Copy-Item $content $backup -Force
    Write-Host "Контент сохранён: $backup"
}

if (Test-Path $InstallDir) {
    Remove-Item $InstallDir -Recurse -Force
    Write-Host "Папка $InstallDir удалена."
}
# Родительскую папку (C:\ClinicHub) убираем, только если в ней больше ничего нет: там могут жить другие части ClinicHub.
$parent = Split-Path $InstallDir -Parent
if ($parent -and (Test-Path $parent) -and -not (Get-ChildItem $parent -Force | Select-Object -First 1)) {
    Remove-Item $parent -Force
}
if ([System.Diagnostics.EventLog]::SourceExists($EventSource)) {
    Remove-EventLog -Source $EventSource
}
$shortcut = Join-Path ([Environment]::GetFolderPath("CommonDesktopDirectory")) "Сайт клиники.url"
Remove-Item $shortcut -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "Сайт удалён." -ForegroundColor Green
Read-Host "Нажмите Enter, чтобы закрыть окно"
