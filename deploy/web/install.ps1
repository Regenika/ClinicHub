# Установка сайта ClinicHub.Web на компьютер клиники (Windows 10/11).
# Запуск: двойной клик по install.cmd (попросит права администратора).
#
# Что делает:
#   1. Проверяет ASP.NET Core Runtime 10 (нужен только для переносимой сборки) и при необходимости ставит его через winget.
#   2. Копирует сайт в C:\ClinicHub\Web. Файл с контентом site-content.json при переустановке не перезаписывает.
#   3. Регистрирует службу Windows «ClinicHub Web» от имени LOCAL SERVICE (без лишних прав): сайт стартует
#      вместе с Windows, без входа пользователя, и перезапускается через минуту, если упал.
#      Журнал — в C:\ClinicHub\Web\logs и в журнале событий Windows «Приложение» (источник ClinicHub.Web).
#   4. Открывает порт в брандмауэре только для частной и доменной сети и только для адресов из локальной подсети.
#   5. Проверяет, что сайт отвечает, и кладёт ярлык на рабочий стол.
#
# Версии до 2026-10 ставили сайт задачей планировщика под SYSTEM — такая задача удаляется автоматически.

param(
    [int]$Port = 8080,
    [string]$InstallDir = "C:\ClinicHub\Web"
)

$ErrorActionPreference = "Stop"
# Если скрипт запустят в PowerShell 7, передаём аргументы внешним программам по-старому, как в 5.1 (см. binPath ниже).
$PSNativeCommandArgumentPassing = "Legacy"
$ServiceName = "ClinicHub Web"
$OldTaskName = "ClinicHub Web"
$FirewallRule = "ClinicHub Web"
$EventSource = "ClinicHub.Web"
$LocalServiceSid = "*S-1-5-19"   # NT AUTHORITY\LOCAL SERVICE; по SID, чтобы не зависеть от языка Windows
$Source = Join-Path $PSScriptRoot "app"

function Step($text) { Write-Host ""; Write-Host "==> $text" -ForegroundColor Cyan }
function Fail($text) {
    Write-Host ""
    Write-Host "ОШИБКА: $text" -ForegroundColor Red
    Read-Host "Нажмите Enter, чтобы закрыть окно"
    exit 1
}

# ── Права администратора ─────────────────────────────────────────────────────
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Start-Process powershell.exe -Verb RunAs -ArgumentList @(
        "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$PSCommandPath`"",
        "-Port", $Port, "-InstallDir", "`"$InstallDir`"")
    exit
}

Write-Host "Установка сайта клиники (ClinicHub.Web)" -ForegroundColor Green
Write-Host "Папка: $InstallDir   Порт: $Port"

if (-not (Test-Path (Join-Path $Source "ClinicHub.Web.dll"))) {
    Fail "Рядом со скриптом нет папки app с сайтом. Распакуйте архив целиком и запустите install.cmd снова."
}

# Автономная сборка содержит ClinicHub.Web.exe и сам .NET; переносимая — только ClinicHub.Web.dll.
$selfContained = Test-Path (Join-Path $Source "hostfxr.dll")

# ── .NET ─────────────────────────────────────────────────────────────────────
function Find-Dotnet {
    $candidates = @("$env:ProgramFiles\dotnet\dotnet.exe")
    $cmd = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if ($cmd) { $candidates = @($cmd.Source) + $candidates }
    foreach ($c in $candidates) {
        if (Test-Path $c) {
            $runtimes = & $c --list-runtimes 2>$null
            if ($runtimes -match "Microsoft\.AspNetCore\.App 10\.") { return $c }
        }
    }
    return $null
}

$dotnet = $null
if (-not $selfContained) {
    Step "Проверяю ASP.NET Core Runtime 10"
    $dotnet = Find-Dotnet
    if (-not $dotnet) {
        $winget = Get-Command winget.exe -ErrorAction SilentlyContinue
        if ($winget) {
            Write-Host "Не установлен. Ставлю через winget (нужен интернет, пара минут)..."
            & winget.exe install --id Microsoft.DotNet.AspNetCore.10 --exact --silent --accept-package-agreements --accept-source-agreements
            $dotnet = Find-Dotnet
        }
        if (-not $dotnet) {
            Start-Process "https://dotnet.microsoft.com/download/dotnet/10.0"
            Fail ("ASP.NET Core Runtime 10 не найден. Открыл страницу загрузки: скачайте «ASP.NET Core Runtime 10» " +
                  "для Windows x64 (Hosting Bundle тоже подойдёт), установите и запустите install.cmd ещё раз.")
        }
    }
    Write-Host "Найден: $dotnet"
}

# ── Остановить прошлую версию ────────────────────────────────────────────────
Step "Останавливаю прошлую версию, если она есть"
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
}
# Прошлые версии установщика запускали сайт задачей планировщика — убираем её, теперь вместо неё служба.
if (Get-ScheduledTask -TaskName $OldTaskName -ErrorAction SilentlyContinue) {
    Stop-ScheduledTask -TaskName $OldTaskName -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $OldTaskName -Confirm:$false
    Write-Host "Старая задача планировщика «$OldTaskName» удалена."
}
# Процессы, запущенные из папки установки, держат файлы — закрываем их.
Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -and $_.CommandLine -like "*$InstallDir*" -and $_.ProcessId -ne $PID } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Start-Sleep -Seconds 2

# ── Копирование ──────────────────────────────────────────────────────────────
Step "Копирую файлы в $InstallDir"
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
$contentFile = Join-Path $InstallDir "site-content.json"
$keepContent = Test-Path $contentFile
if ($keepContent) {
    Copy-Item $contentFile "$contentFile.bak" -Force
}
# Старые файлы версии убираем, чтобы не оставались лишние (контент сохранён выше, журналы не трогаем).
Get-ChildItem $InstallDir -Force | Where-Object { $_.Name -notlike "site-content.json*" -and $_.Name -ne "logs" } |
    Remove-Item -Recurse -Force
Copy-Item (Join-Path $Source "*") $InstallDir -Recurse -Force -Exclude "site-content.json"
if ($keepContent) {
    Write-Host "site-content.json оставлен прежним (ваши правки контента сохранены, копия: site-content.json.bak)."
} else {
    Copy-Item (Join-Path $Source "site-content.json") $contentFile -Force
}

# ── Права на папку ───────────────────────────────────────────────────────────
# Служба работает от LOCAL SERVICE: сайт может только читать свои файлы, писать — только в logs.
$logsDir = Join-Path $InstallDir "logs"
New-Item -ItemType Directory -Force -Path $logsDir | Out-Null
& icacls.exe $InstallDir /grant "${LocalServiceSid}:(OI)(CI)RX" /T /C /Q | Out-Null
if ($LASTEXITCODE -ne 0) { Fail "Не удалось выдать службе права на папку $InstallDir." }
& icacls.exe $logsDir /grant "${LocalServiceSid}:(OI)(CI)M" /T /C /Q | Out-Null
if ($LASTEXITCODE -ne 0) { Fail "Не удалось выдать службе права на папку $logsDir." }

# Источник для журнала событий Windows: создать его может только администратор, поэтому делает установщик.
if (-not [System.Diagnostics.EventLog]::SourceExists($EventSource)) {
    New-EventLog -LogName Application -Source $EventSource
}

# ── Служба ───────────────────────────────────────────────────────────────────
Step "Настраиваю автозапуск (служба Windows «$ServiceName»)"
# http://* — и IPv4, и IPv6: иначе localhost (сначала ::1) отвечает с задержкой в 2 секунды.
$appArgs = "--urls http://*:$Port --contentRoot `"$InstallDir`""
if ($selfContained) {
    $binPath = "`"$(Join-Path $InstallDir 'ClinicHub.Web.exe')`" $appArgs"
} else {
    $binPath = "`"$dotnet`" `"$(Join-Path $InstallDir 'ClinicHub.Web.dll')`" $appArgs"
}

# sc.exe, а не New-Service: только так в PowerShell 5.1 можно задать LOCAL SERVICE и действия при сбое.
# PowerShell 5.1 не экранирует кавычки внутри аргументов внешних программ, поэтому в binPath они экранированы вручную (\").
$binPathArg = $binPath.Replace('"', '\"')
function Invoke-Sc([string[]]$scArgs, [string]$what) {
    $out = & sc.exe @scArgs 2>&1
    if ($LASTEXITCODE -ne 0) { Fail "$what (sc.exe, код $LASTEXITCODE): $out" }
}
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    # Учётную запись не меняем: служба уже создана этим установщиком от LOCAL SERVICE.
    Invoke-Sc @("config", $ServiceName, "binPath=", $binPathArg, "start=", "delayed-auto") "Не удалось обновить службу"
} else {
    Invoke-Sc @("create", $ServiceName, "binPath=", $binPathArg, "start=", "delayed-auto",
         "obj=", "NT AUTHORITY\LocalService", "DisplayName=", $ServiceName) "Не удалось создать службу"
}
Invoke-Sc @("description", $ServiceName, "Сайт клиники (ClinicHub.Web), порт $Port") "Не удалось задать описание службы"
# Если сайт упал — перезапуск через минуту, счётчик сбоев обнуляется раз в сутки.
Invoke-Sc @("failure", $ServiceName, "reset=", "86400", "actions=", "restart/60000/restart/60000/restart/60000") `
    "Не удалось настроить перезапуск службы"
# Перезапускать и тогда, когда сайт сам остановился с ошибкой (например, порт занят), а не только при аварии процесса.
Invoke-Sc @("failureflag", $ServiceName, "1") "Не удалось настроить перезапуск службы"
try {
    Start-Service -Name $ServiceName
} catch {
    Fail ("Служба «$ServiceName» не запустилась: $($_.Exception.Message) " +
          "Причину смотрите в журнале $logsDir и в журнале событий Windows «Приложение», источник $EventSource.")
}

# ── Брандмауэр ───────────────────────────────────────────────────────────────
# Только частная и доменная сеть и только адреса из локальной подсети: в общественном Wi-Fi порт закрыт.
Step "Открываю порт $Port в брандмауэре Windows (только локальная сеть)"
Get-NetFirewallRule -DisplayName $FirewallRule -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule -DisplayName $FirewallRule -Direction Inbound -Protocol TCP -LocalPort $Port `
    -Action Allow -Profile Private, Domain -RemoteAddress LocalSubnet | Out-Null

$publicNets = @(Get-NetConnectionProfile -ErrorAction SilentlyContinue | Where-Object NetworkCategory -eq "Public")

# ── Проверка ─────────────────────────────────────────────────────────────────
Step "Жду, пока сайт запустится"
$local = "http://localhost:$Port/"
$ok = $false
for ($i = 0; $i -lt 30; $i++) {
    try {
        $r = Invoke-WebRequest -Uri $local -UseBasicParsing -TimeoutSec 3
        if ($r.StatusCode -eq 200) { $ok = $true; break }
    } catch { }
    Start-Sleep -Seconds 1
}
if (-not $ok) {
    Fail ("Сайт не ответил за 30 секунд. Проверьте, не занят ли порт $Port другой программой " +
          "(тогда запустите install.cmd с другим портом, см. README.txt). " +
          "Причину смотрите в журнале $logsDir и в журнале событий Windows «Приложение», источник $EventSource.")
}

# Ярлык на общем рабочем столе.
$shortcut = Join-Path ([Environment]::GetFolderPath("CommonDesktopDirectory")) "Сайт клиники.url"
Set-Content -Path $shortcut -Value "[InternetShortcut]`r`nURL=$local" -Encoding ASCII

$ips = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
    Where-Object { $_.IPAddress -notlike "127.*" -and $_.IPAddress -notlike "169.254.*" } |
    Select-Object -ExpandProperty IPAddress

Write-Host ""
Write-Host "Готово! Сайт работает и будет запускаться вместе с компьютером." -ForegroundColor Green
Write-Host "  На этом компьютере:      $local"
foreach ($ip in $ips) { Write-Host "  С других компьютеров:    http://${ip}:$Port/" }
if ($publicNets.Count -gt 0) {
    Write-Host ""
    Write-Host "ВНИМАНИЕ: сеть «$($publicNets[0].Name)» отмечена в Windows как общественная." -ForegroundColor Yellow
    Write-Host "С других компьютеров сайт будет недоступен, пока сеть не станет частной. Если это сеть клиники," -ForegroundColor Yellow
    Write-Host "в PowerShell от администратора выполните:" -ForegroundColor Yellow
    Write-Host "  Set-NetConnectionProfile -InterfaceIndex $($publicNets[0].InterfaceIndex) -NetworkCategory Private" -ForegroundColor Yellow
}
Write-Host ""
Write-Host "Журнал работы сайта: $logsDir"
Write-Host "Контент (телефон, адрес, услуги, врачи): $contentFile — правится в Блокноте, перезапуск не нужен."
Start-Process $local
Read-Host "Нажмите Enter, чтобы закрыть окно"
