# Установка сайта ClinicHub.Web на компьютер клиники (Windows 10/11).
# Запуск: двойной клик по install.cmd (попросит права администратора).
#
# Что делает:
#   1. Проверяет ASP.NET Core Runtime 10 (нужен только для переносимой сборки) и при необходимости ставит его через winget.
#   2. Копирует сайт в C:\ClinicHub\Web. Файл с контентом site-content.json при переустановке не перезаписывает.
#   3. Регистрирует службу Windows «ClinicHub Web» от имени LOCAL SERVICE (без лишних прав): сайт стартует
#      вместе с Windows, без входа пользователя, и перезапускается через минуту, если упал.
#      Журнал — в C:\ClinicHub\Web\logs и в журнале событий Windows «Приложение» (источник ClinicHub.Web).
#      Подключение к SQL Server задаётся в браузере на странице http://localhost:<порт>/nastroika-bd и хранится
#      зашифрованным в C:\ClinicHub\Web\config (при переустановке сохраняется).
#   4. Открывает порт в брандмауэре только для частной и доменной сети и только для адресов из локальной подсети.
#   5. Проверяет, что сайт отвечает, и кладёт ярлык на рабочий стол.
#
# Версии до 2026-10 ставили сайт задачей планировщика под SYSTEM — такая задача удаляется автоматически.
#
# -Unattended — без вопросов и «Нажмите Enter», без открытия браузера; итог — код выхода (0 — успех).
# Так установщик запускает автообновление (auto-update.ps1).

param(
    [int]$Port = 8080,
    [string]$InstallDir = "C:\ClinicHub\Web",
    [switch]$Unattended
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
    if (-not $Unattended) { Read-Host "Нажмите Enter, чтобы закрыть окно" }
    exit 1
}

# ── Права администратора ─────────────────────────────────────────────────────
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    $elevatedArgs = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$PSCommandPath`"",
        "-Port", $Port, "-InstallDir", "`"$InstallDir`"")
    if ($Unattended) {
        # Без окна: ждём окончания и возвращаем код выхода установки.
        $p = Start-Process powershell.exe -Verb RunAs -Wait -PassThru -WindowStyle Hidden -ArgumentList ($elevatedArgs + "-Unattended")
        exit $p.ExitCode
    }
    Start-Process powershell.exe -Verb RunAs -ArgumentList $elevatedArgs
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
# Старые файлы версии убираем, чтобы не оставались лишние (контент сохранён выше; журналы и подключение к базе не трогаем).
Get-ChildItem $InstallDir -Force | Where-Object { $_.Name -notlike "site-content.json*" -and $_.Name -notin "logs", "config" } |
    Remove-Item -Recurse -Force
Copy-Item (Join-Path $Source "*") $InstallDir -Recurse -Force -Exclude "site-content.json"
if ($keepContent) {
    Write-Host "site-content.json оставлен прежним (ваши правки контента сохранены, копия: site-content.json.bak)."
} else {
    Copy-Item (Join-Path $Source "site-content.json") $contentFile -Force
}

# ── Права на папку ───────────────────────────────────────────────────────────
# Служба работает от LOCAL SERVICE: сайт может только читать свои файлы, писать — только в logs и config.
# В config — подключение к базе и ключи шифрования: читать их могут только служба и администраторы.
$logsDir = Join-Path $InstallDir "logs"
$configDir = Join-Path $InstallDir "config"
New-Item -ItemType Directory -Force -Path $logsDir, $configDir | Out-Null
& icacls.exe $InstallDir /grant "${LocalServiceSid}:(OI)(CI)RX" /T /C /Q | Out-Null
if ($LASTEXITCODE -ne 0) { Fail "Не удалось выдать службе права на папку $InstallDir." }
& icacls.exe $logsDir /grant "${LocalServiceSid}:(OI)(CI)M" /T /C /Q | Out-Null
if ($LASTEXITCODE -ne 0) { Fail "Не удалось выдать службе права на папку $logsDir." }
# Наследование прав отключаем: обычные пользователи компьютера не должны видеть файл подключения.
# Права ставим только на папку (без /T: флаги (OI)(CI) к файлам не применяются, и файлы остались бы совсем без прав),
# а файлам внутри возвращаем наследование от неё — при переустановке поверх служба по-прежнему их читает.
& icacls.exe $configDir /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" "${LocalServiceSid}:(OI)(CI)M" /C /Q | Out-Null
if ($LASTEXITCODE -ne 0) { Fail "Не удалось выдать службе права на папку $configDir." }
if (Get-ChildItem $configDir -Force | Select-Object -First 1) {
    # Файлы создаёт служба, она и владелец. Если права на них испорчены (так делала версия установщика до 2026-10-17),
    # администратор не может их поменять, пока не станет владельцем: сначала владелец — группа «Администраторы».
    # takeown, а не icacls /setowner: только он пускает к файлу без единого разрешения (привилегия «смена владельца»).
    Get-ChildItem $configDir -Recurse -File -Force | ForEach-Object { & takeown.exe /F $_.FullName /A | Out-Null }
    & icacls.exe (Join-Path $configDir "*") /reset /T /C /Q | Out-Null
    if ($LASTEXITCODE -ne 0) { Fail "Не удалось восстановить права на файлы в $configDir." }
}

# Источник для журнала событий Windows: создать его может только администратор, поэтому делает установщик.
if (-not [System.Diagnostics.EventLog]::SourceExists($EventSource)) {
    New-EventLog -LogName Application -Source $EventSource
}

# ── Порт ─────────────────────────────────────────────────────────────────────
# Hyper-V, WSL и Docker (служба WinNAT) после перезагрузки резервируют за собой случайные диапазоны портов. Если порт сайта
# попал в такой диапазон, служба не стартует («порт занят», так было на Valsot-PC 2026-10-20). Поэтому закрепляем порт
# за сайтом своей резервацией: её Windows не раздаёт другим, а слушать порт сайту она не мешает.
function Get-PortExclusion([string]$family) {
    # "administered" — наша резервация (в выводе netsh со звёздочкой), "system" — диапазон, который взяла Windows, $null — свободен.
    foreach ($line in (& netsh.exe int $family show excludedportrange protocol=tcp)) {
        if ($line -match '^\s*(\d+)\s+(\d+)(\s+\*)?\s*$' -and $Port -ge [int]$Matches[1] -and $Port -le [int]$Matches[2]) {
            if ($Matches[3]) { return "administered" } else { return "system" }
        }
    }
    return $null
}
Step "Закрепляю порт $Port за сайтом"
$families = @("ipv4", "ipv6")
$missing = @($families | Where-Object { (Get-PortExclusion $_) -ne "administered" })
if ($missing.Count -gt 0) {
    # Диапазон, который уже взяла Windows, освобождается только остановкой WinNAT; после запуска она возьмёт другие порты.
    $winnat = Get-Service winnat -ErrorAction SilentlyContinue
    $restartWinnat = $winnat -and $winnat.Status -eq "Running" -and @($missing | Where-Object { (Get-PortExclusion $_) -eq "system" }).Count -gt 0
    if ($restartWinnat) {
        Write-Host "Порт попал в резерв Windows (WinNAT) — перезапускаю WinNAT, чтобы его освободить."
        Stop-Service winnat -Force
    }
    try {
        foreach ($f in $missing) {
            # Обычно список у ipv4 и ipv6 общий: после ipv4 порт уже закреплён и для ipv6, повторное добавление — ошибка.
            if ((Get-PortExclusion $f) -eq "administered") { continue }
            $out = & netsh.exe int $f add excludedportrange protocol=tcp startport=$Port numberofports=1 store=persistent 2>&1
            if ($LASTEXITCODE -ne 0) { Fail "Не удалось закрепить порт $Port ($f): $out" }
        }
    } finally {
        if ($restartWinnat) { Start-Service winnat }
    }
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
    # Любой HTTP-ответ значит, что служба работает. 503 — сайт ещё подключается к базе данных (в фоне, это нормально).
    try {
        $r = Invoke-WebRequest -Uri $local -UseBasicParsing -TimeoutSec 3
        if ($r.StatusCode -eq 200) { $ok = $true; break }
    } catch [System.Net.WebException] {
        if ($_.Exception.Response) { $ok = $true; break }
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
if ($Unattended) { exit 0 }
Write-Host ""
Write-Host "Если сайт ещё не подключён к базе данных, в браузере откроется страница подключения:" -ForegroundColor Cyan
Write-Host "  ${local}nastroika-bd — укажите сервер SQL Server, логин и пароль." -ForegroundColor Cyan
Start-Process $local
Read-Host "Нажмите Enter, чтобы закрыть окно"
