# Обновление ClinicHub на компьютере клиники: сервер (api) и сайт (web) из этого архива, без вопросов.
# Запуск: двойной клик по update.cmd (попросит права администратора).
# Подключение к базе, ключи, тексты сайта и журналы остаются от прошлой установки.
# Первую установку делают install.cmd в папках api и web (deploy\README.txt).

$ErrorActionPreference = "Stop"

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Start-Process powershell.exe -Verb RunAs -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$PSCommandPath`"")
    return
}

function Done([string]$text, [string]$color) {
    Write-Host ""
    Write-Host $text -ForegroundColor $color
    Read-Host "Нажмите Enter, чтобы закрыть окно"
}

$version = if (Test-Path (Join-Path $PSScriptRoot "VERSION.txt")) { (Get-Content (Join-Path $PSScriptRoot "VERSION.txt") -Raw).Trim() } else { "?" }
Write-Host "Обновление ClinicHub до версии $version" -ForegroundColor Cyan

if (-not (Get-Service -Name "ClinicHub API" -ErrorAction SilentlyContinue)) {
    Done "Сервер ClinicHub на этом компьютере не установлен. Сначала первая установка: api\install.cmd, затем web\install.cmd." Yellow
    return
}

foreach ($part in @(@{ Dir = "api"; Name = "Сервер" }, @{ Dir = "web"; Name = "Сайт" })) {
    Write-Host ""
    Write-Host "── $($part.Name) ──" -ForegroundColor Cyan
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "$($part.Dir)\install.ps1") -Unattended
    if ($LASTEXITCODE -ne 0) {
        Done "$($part.Name): обновление не удалось (код $LASTEXITCODE). Журнал — C:\ClinicHub\$($part.Dir)\logs. Прошлые настройки не тронуты." Red
        return
    }
}

# Проверка: сервер отвечает, сайт открывается, панель рассылок достучалась до сервера.
Start-Sleep -Seconds 5
$ok = $true
try {
    $health = Invoke-RestMethod -Uri "http://localhost:5112/api/health" -TimeoutSec 20
    Write-Host "Сервер: $($health.status), база: $($health.database)"
} catch { Write-Host "Сервер не ответил на http://localhost:5112/api/health" -ForegroundColor Yellow; $ok = $false }
try {
    $null = Invoke-WebRequest -Uri "http://localhost:8080/" -UseBasicParsing -TimeoutSec 30
    Write-Host "Сайт: открывается"
} catch { Write-Host "Сайт не открылся на http://localhost:8080" -ForegroundColor Yellow; $ok = $false }
if (-not (Test-Path (Join-Path $env:ProgramData "ClinicHub\panel.key"))) {
    Write-Host "Ключ панели рассылок ещё не создан — сервер создаёт его при запуске. Если через минуту" -ForegroundColor Yellow
    Write-Host "в админке «SMS» пишет «нет связи с сервером», перезапустите службу «ClinicHub API»." -ForegroundColor Yellow
}

if ($ok) {
    Done "Готово. Админка: http://localhost:8080/admin → раздел «SMS»." Green
} else {
    Done "Обновление установлено, но проверка не прошла — см. сообщения выше." Yellow
}
