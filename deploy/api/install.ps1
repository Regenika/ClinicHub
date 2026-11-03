# Установка сервера ClinicHub.Api на компьютер клиники (Windows 10/11), Issue #48.
# Запуск: двойной клик по install.cmd (попросит права администратора).
#
# Что делает (по образцу установщика сайта deploy/web):
#   1. Копирует сервер в C:\ClinicHub\Api.
#   2. При первой установке спрашивает подключение к базе (SQL Server, обычно на другом компьютере) и ключи
#      Dikidi (можно пропустить), сам создаёт секрет токенов входа (Jwt). Кладёт их в config\secrets.json, доступный
#      только системе, администраторам и службе; при запуске сервер шифрует их (DPAPI) в config\secrets.dat, а открытый
#      файл удаляет. В журнал и на экран значения не выводятся.
#   3. Регистрирует службу Windows «ClinicHub API» от LOCAL SERVICE: стартует с Windows, перезапуск через минуту.
#      Журнал — C:\ClinicHub\Api\logs и журнал событий Windows «Приложение» (источник ClinicHub.Api).
#   4. Закрепляет порт (5112) и открывает его в брандмауэре только для частной и доменной сети и только для
#      локальной подсети; так же — UDP 45454 для автопоиска сервера телефоном. Старые правила, которые Windows
#      создала сама при первом запуске ClinicHub.Api.exe (в том числе для общественной сети), удаляет.
#   5. Проверяет, что сервер отвечает, и показывает адреса: для сайта (Api:BaseUrl), админки и телефонов.
#
# База может быть недоступна (VPN ещё не поднят) — сервер всё равно запустится и будет пробовать подключиться
# каждые 30 секунд, причина — в журнале и на http://localhost:5112/api/debug/state.
#
# -ChangeSecrets — спросить подключение к базе и Dikidi заново (например, сменился пароль базы).
# -Unattended — без вопросов (так ставит автообновление); секреты должны быть уже заданы прошлой установкой.

param(
    [int]$Port = 5112,
    [string]$InstallDir = "C:\ClinicHub\Api",
    [switch]$ChangeSecrets,
    [switch]$Unattended
)

$ErrorActionPreference = "Stop"
$PSNativeCommandArgumentPassing = "Legacy"
$ServiceName = "ClinicHub API"
$FirewallRule = "ClinicHub API"
$DiscoveryRule = "ClinicHub discovery"
$DiscoveryPort = 45454
$EventSource = "ClinicHub.Api"
$LocalServiceSid = "*S-1-5-19"   # NT AUTHORITY\LOCAL SERVICE; по SID, чтобы не зависеть от языка Windows
$Source = Join-Path $PSScriptRoot "app"

# Порт не указан, а сервер уже установлен (переустановка, автообновление) — оставляем прежний порт службы.
if (-not $PSBoundParameters.ContainsKey("Port")) {
    $existing = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue
    if ($existing -and $existing.PathName -match '--urls http://\*:(\d+)') { $Port = [int]$Matches[1] }
}

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
    if ($ChangeSecrets) { $elevatedArgs += "-ChangeSecrets" }
    if ($Unattended) {
        $p = Start-Process powershell.exe -Verb RunAs -Wait -PassThru -WindowStyle Hidden -ArgumentList ($elevatedArgs + "-Unattended")
        exit $p.ExitCode
    }
    Start-Process powershell.exe -Verb RunAs -ArgumentList $elevatedArgs
    exit
}

Write-Host "Установка сервера рассылок (ClinicHub.Api)" -ForegroundColor Green
Write-Host "Папка: $InstallDir   Порт: $Port"

$exe = Join-Path $Source "ClinicHub.Api.exe"
if (-not (Test-Path $exe)) {
    Fail "Рядом со скриптом нет папки app с сервером. Распакуйте архив целиком и запустите install.cmd снова."
}

$configDir = Join-Path $InstallDir "config"
$logsDir = Join-Path $InstallDir "logs"
$secretsDat = Join-Path $configDir "secrets.dat"
$secretsJson = Join-Path $configDir "secrets.json"
$haveSecrets = (Test-Path $secretsDat) -or (Test-Path $secretsJson)
$askSecrets = $ChangeSecrets -or -not $haveSecrets
if ($askSecrets -and $Unattended) {
    Fail "Секреты сервера ещё не заданы. Запустите install.cmd вручную: он спросит подключение к базе."
}

# ── Секреты (спрашиваем до остановки прошлой версии: если передумали, сервер продолжает работать) ──
$secrets = [ordered]@{}
if ($askSecrets) {
    Step "Подключение к базе данных"
    Write-Host "SQL Server с базой ClinicHub обычно стоит на другом компьютере (доступ по VPN или туннелю)."
    Write-Host "Нужен логин SQL Server (вход Windows отсюда к другому компьютеру не подойдёт)."
    $server = Read-Host "Сервер (например 10.8.1.1,1433 или 192.168.1.10\SQLEXPRESS)"
    $database = Read-Host "База данных [ClinicHub]"
    if ([string]::IsNullOrWhiteSpace($database)) { $database = "ClinicHub" }
    $login = Read-Host "Логин SQL Server"
    $securePassword = Read-Host "Пароль" -AsSecureString
    $password = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($securePassword))
    if ([string]::IsNullOrWhiteSpace($server) -or [string]::IsNullOrWhiteSpace($login)) {
        Fail "Нужны адрес сервера и логин SQL Server."
    }

    $csb = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
    $csb["Data Source"] = $server.Trim()
    $csb["Initial Catalog"] = $database.Trim()
    $csb["User ID"] = $login.Trim()
    $csb["Password"] = $password
    $csb["TrustServerCertificate"] = $true
    $csb["MultipleActiveResultSets"] = $true
    $csb["Connect Timeout"] = 15

    Write-Host "Проверяю подключение..."
    $connection = New-Object System.Data.SqlClient.SqlConnection($csb.ConnectionString)
    try {
        $connection.Open()
        Write-Host "Подключение есть." -ForegroundColor Green
    } catch {
        $reason = if ($_.Exception.InnerException) { $_.Exception.InnerException.Message } else { $_.Exception.Message }
        Write-Host "Не удалось подключиться: $reason" -ForegroundColor Yellow
        Write-Host "Если база сейчас просто недоступна (VPN не поднят), можно продолжить: сервер будет подключаться сам каждые 30 секунд." -ForegroundColor Yellow
        $answer = Read-Host "Продолжить установку? (д/н)"
        if ($answer -notmatch '^(д|y)') { Fail "Установка отменена: проверьте адрес, логин и пароль базы и запустите install.cmd снова." }
    } finally {
        $connection.Dispose()
    }
    $secrets["ConnectionStrings:ClinicHub"] = $csb.ConnectionString

    # Секрет токенов входа: случайный, задаётся один раз. При смене секретов прежний сохраняется в secrets.dat
    # (ключи, которых нет в secrets.json, сервер не трогает) — иначе всем пришлось бы войти заново.
    if (-not $haveSecrets) {
        $bytes = New-Object byte[] 48
        [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
        $secrets["Jwt:Secret"] = [Convert]::ToBase64String($bytes)
    }

    Step "Dikidi (журнал записи, только чтение)"
    $dikidi = Read-Host "Подключить Dikidi сейчас? Нужна отдельная учётка сотрудника (д/н) [н]"
    if ($dikidi -match '^(д|y)') {
        $secrets["Dikidi:Phone"] = Read-Host "Телефон учётки Dikidi"
        $dikidiPassword = Read-Host "Пароль учётки Dikidi" -AsSecureString
        $secrets["Dikidi:Password"] = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($dikidiPassword))
        $secrets["Dikidi:CompanyId"] = Read-Host "ID компании (число из адреса журнала ...company=)"
    } elseif ($ChangeSecrets) {
        # Пустые значения сервер понимает как «удалить ключ» — Dikidi выключится.
        $secrets["Dikidi:Phone"] = ""
        $secrets["Dikidi:Password"] = ""
        $secrets["Dikidi:CompanyId"] = ""
    }
}

# ── Остановить прошлую версию ────────────────────────────────────────────────
Step "Останавливаю прошлую версию, если она есть"
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
}
Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -and $_.CommandLine -like "*$InstallDir*" -and $_.ProcessId -ne $PID } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Start-Sleep -Seconds 2

# ── Копирование ──────────────────────────────────────────────────────────────
Step "Копирую файлы в $InstallDir"
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
# Журналы и секреты не трогаем, остальное заменяем целиком.
Get-ChildItem $InstallDir -Force | Where-Object { $_.Name -notin "logs", "config" } | Remove-Item -Recurse -Force
Copy-Item (Join-Path $Source "*") $InstallDir -Recurse -Force

# ── Права на папку ───────────────────────────────────────────────────────────
# Служба (LOCAL SERVICE) читает свои файлы, пишет только в logs и config. config видят только служба и администраторы.
New-Item -ItemType Directory -Force -Path $logsDir, $configDir | Out-Null
& icacls.exe $InstallDir /grant "${LocalServiceSid}:(OI)(CI)RX" /T /C /Q | Out-Null
if ($LASTEXITCODE -ne 0) { Fail "Не удалось выдать службе права на папку $InstallDir." }
& icacls.exe $logsDir /grant "${LocalServiceSid}:(OI)(CI)M" /T /C /Q | Out-Null
if ($LASTEXITCODE -ne 0) { Fail "Не удалось выдать службе права на папку $logsDir." }
& icacls.exe $configDir /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" "${LocalServiceSid}:(OI)(CI)M" /C /Q | Out-Null
if ($LASTEXITCODE -ne 0) { Fail "Не удалось выдать службе права на папку $configDir." }
if (Get-ChildItem $configDir -Force | Select-Object -First 1) {
    # Файлы (ключи, secrets.dat) создаёт служба, она и владелец; возвращаем им права от папки (как у сайта).
    Get-ChildItem $configDir -Recurse -File -Force | ForEach-Object { & takeown.exe /F $_.FullName /A | Out-Null }
    & icacls.exe (Join-Path $configDir "*") /reset /T /C /Q | Out-Null
    if ($LASTEXITCODE -ne 0) { Fail "Не удалось восстановить права на файлы в $configDir." }
}

if ($secrets.Count -gt 0) {
    # Файл создаётся внутри config и наследует её права. Сервер при запуске зашифрует его и удалит.
    $json = $secrets | ConvertTo-Json -Compress
    [IO.File]::WriteAllText($secretsJson, $json, (New-Object Text.UTF8Encoding($false)))
    Write-Host "Секреты записаны (значения не показываются). Сервер зашифрует их при запуске."
}

if (-not [System.Diagnostics.EventLog]::SourceExists($EventSource)) {
    New-EventLog -LogName Application -Source $EventSource
}

# ── Порт ─────────────────────────────────────────────────────────────────────
# Как у сайта (#33): Hyper-V, WSL и Docker после перезагрузки резервируют случайные диапазоны портов.
function Get-PortExclusion([string]$family) {
    foreach ($line in (& netsh.exe int $family show excludedportrange protocol=tcp)) {
        if ($line -match '^\s*(\d+)\s+(\d+)(\s+\*)?\s*$' -and $Port -ge [int]$Matches[1] -and $Port -le [int]$Matches[2]) {
            if ($Matches[3]) { return "administered" } else { return "system" }
        }
    }
    return $null
}
Step "Закрепляю порт $Port за сервером"
$families = @("ipv4", "ipv6")
$missing = @($families | Where-Object { (Get-PortExclusion $_) -ne "administered" })
if ($missing.Count -gt 0) {
    $winnat = Get-Service winnat -ErrorAction SilentlyContinue
    $restartWinnat = $winnat -and $winnat.Status -eq "Running" -and @($missing | Where-Object { (Get-PortExclusion $_) -eq "system" }).Count -gt 0
    if ($restartWinnat) {
        Write-Host "Порт попал в резерв Windows (WinNAT) — перезапускаю WinNAT, чтобы его освободить."
        Stop-Service winnat -Force
    }
    try {
        foreach ($f in $missing) {
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
$binPath = "`"$(Join-Path $InstallDir 'ClinicHub.Api.exe')`" --urls http://*:$Port --contentRoot `"$InstallDir`""
$binPathArg = $binPath.Replace('"', '\"')
function Invoke-Sc([string[]]$scArgs, [string]$what) {
    $out = & sc.exe @scArgs 2>&1
    if ($LASTEXITCODE -ne 0) { Fail "$what (sc.exe, код $LASTEXITCODE): $out" }
}
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Invoke-Sc @("config", $ServiceName, "binPath=", $binPathArg, "start=", "delayed-auto") "Не удалось обновить службу"
} else {
    Invoke-Sc @("create", $ServiceName, "binPath=", $binPathArg, "start=", "delayed-auto",
         "obj=", "NT AUTHORITY\LocalService", "DisplayName=", $ServiceName) "Не удалось создать службу"
}
Invoke-Sc @("description", $ServiceName, "Сервер рассылок клиники (ClinicHub.Api), порт $Port") "Не удалось задать описание службы"
Invoke-Sc @("failure", $ServiceName, "reset=", "86400", "actions=", "restart/60000/restart/60000/restart/60000") `
    "Не удалось настроить перезапуск службы"
Invoke-Sc @("failureflag", $ServiceName, "1") "Не удалось настроить перезапуск службы"
try {
    Start-Service -Name $ServiceName
} catch {
    Fail ("Служба «$ServiceName» не запустилась: $($_.Exception.Message) " +
          "Причину смотрите в журнале $logsDir и в журнале событий Windows «Приложение», источник $EventSource.")
}

# ── Брандмауэр ───────────────────────────────────────────────────────────────
Step "Открываю порт $Port и автопоиск (UDP $DiscoveryPort) только для локальной сети"
# Правила, которые Windows создала сама при первом запуске ClinicHub.Api.exe (по имени программы, часто и для
# общественной сети), и правила из старой инструкции README (profile=any) убираем: дальше — только наши.
Get-NetFirewallRule -ErrorAction SilentlyContinue |
    Where-Object { $_.DisplayName -like "clinichub.api*" -or $_.DisplayName -in @($FirewallRule, $DiscoveryRule) } |
    Remove-NetFirewallRule
New-NetFirewallRule -DisplayName $FirewallRule -Direction Inbound -Protocol TCP -LocalPort $Port `
    -Action Allow -Profile Private, Domain -RemoteAddress LocalSubnet | Out-Null
New-NetFirewallRule -DisplayName $DiscoveryRule -Direction Inbound -Protocol UDP -LocalPort $DiscoveryPort `
    -Action Allow -Profile Private, Domain -RemoteAddress LocalSubnet | Out-Null

$publicNets = @(Get-NetConnectionProfile -ErrorAction SilentlyContinue | Where-Object NetworkCategory -eq "Public")

# ── Проверка ─────────────────────────────────────────────────────────────────
Step "Жду, пока сервер запустится"
$health = $null
for ($i = 0; $i -lt 30; $i++) {
    try {
        $health = Invoke-RestMethod -Uri "http://localhost:$Port/api/health" -TimeoutSec 3
        if ($health.status -eq "ok") { break }
    } catch { }
    Start-Sleep -Seconds 1
}
if (-not $health) {
    Fail ("Сервер не ответил за 30 секунд. Проверьте, не занят ли порт $Port другой программой " +
          "(тогда install.cmd 5113, см. README.txt). " +
          "Причину смотрите в журнале $logsDir и в журнале событий Windows «Приложение», источник $EventSource.")
}
# База подключается в фоне: дадим ей немного времени, чтобы сказать, есть ли связь.
for ($i = 0; $i -lt 20 -and $health.database -ne "ok"; $i++) {
    Start-Sleep -Seconds 1
    try { $health = Invoke-RestMethod -Uri "http://localhost:$Port/api/health" -TimeoutSec 3 } catch { }
}

$ips = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
    Where-Object { $_.IPAddress -notlike "127.*" -and $_.IPAddress -notlike "169.254.*" } |
    Select-Object -ExpandProperty IPAddress

Write-Host ""
Write-Host "Готово! Сервер работает и будет запускаться вместе с компьютером." -ForegroundColor Green
if ($health.database -eq "ok") {
    Write-Host "  База данных: подключена." -ForegroundColor Green
} else {
    Write-Host "  База данных: пока нет связи. Сервер пробует каждые 30 секунд; причина — на этом компьютере:" -ForegroundColor Yellow
    Write-Host "  http://localhost:$Port/api/debug/state" -ForegroundColor Yellow
}
Write-Host "  Адрес сервера на этом компьютере (для сайта, Api:BaseUrl, и программы администратора): http://localhost:$Port"
foreach ($ip in $ips) { Write-Host "  Для телефонов с SmsAgent в этой сети:  http://${ip}:$Port" }
if ($publicNets.Count -gt 0) {
    Write-Host ""
    Write-Host "ВНИМАНИЕ: сеть «$($publicNets[0].Name)» отмечена в Windows как общественная — в ней порт закрыт." -ForegroundColor Yellow
    Write-Host "Если это Wi-Fi клиники, в которой работают телефоны, в PowerShell от администратора выполните:" -ForegroundColor Yellow
    Write-Host "  Set-NetConnectionProfile -InterfaceIndex $($publicNets[0].InterfaceIndex) -NetworkCategory Private" -ForegroundColor Yellow
    Write-Host "(VPN до базы данных частной делать не нужно.)" -ForegroundColor Yellow
}
Write-Host ""
Write-Host "Первый администратор в пустой базе создаётся со случайным паролем — он записан в журнал:"
Write-Host "  $logsDir\clinichub.log (строка «Создан первый администратор»)"
Write-Host "Журнал работы сервера: $logsDir"
if ($Unattended) { exit 0 }
Write-Host ""
Read-Host "Нажмите Enter, чтобы закрыть окно"
