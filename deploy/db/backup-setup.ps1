# Ночные резервные копии базы ClinicHub — для КОМПЬЮТЕРА С SQL SERVER (у Свята дома), а не для компьютера клиники (#48).
#
# Включить (PowerShell от имени администратора; ваша учётная запись Windows должна быть администратором SQL Server):
#   powershell -ExecutionPolicy Bypass -File deploy\db\backup-setup.ps1 -Register
#   параметры: -Instance .\SQLEXPRESS  -Database ClinicHub  -BackupDir D:\ClinicHub-Backups  -Keep 14  -At 03:00
# Сделать копию сейчас (проверка):  ... -RunNow
# Выключить (копии остаются):         ... -Unregister
#
# Как работает: задача планировщика «ClinicHub DB Backup» от SYSTEM раз в сутки запускает копию этого скрипта
# (C:\ProgramData\ClinicHub\Backup). Он делает BACKUP DATABASE … WITH CHECKSUM в файл <база>-<дата>-<время>.bak,
# проверяет его RESTORE VERIFYONLY и оставляет Keep последних копий. Журнал — backup.log в папке копий.
# Файл копии пишет сама служба SQL Server, поэтому скрипт даёт ей право на папку.
# Как восстановить базу из копии — deploy\db\RESTORE.txt.

param(
    [switch]$Register,
    [switch]$Unregister,
    [switch]$RunNow,
    [switch]$RunBackup,               # так запускает задача планировщика
    [string]$Instance = ".\SQLEXPRESS",
    [string]$Database = "ClinicHub",
    [string]$BackupDir = "C:\ClinicHub-Backups",
    [ValidateRange(1, 365)][int]$Keep = 14,
    [string]$At = "03:00"
)

$ErrorActionPreference = "Stop"
$TaskName = "ClinicHub DB Backup"
$Root = Join-Path $env:ProgramData "ClinicHub\Backup"
$ConfigFile = Join-Path $Root "backup-config.json"

function Invoke-Sql([string]$instance, [string]$sql, [hashtable]$params = @{}) {
    $cs = "Data Source=$instance;Initial Catalog=master;Integrated Security=True;Connect Timeout=15"
    $conn = New-Object System.Data.SqlClient.SqlConnection($cs)
    try {
        $conn.Open()
        $cmd = $conn.CreateCommand()
        $cmd.CommandText = $sql
        $cmd.CommandTimeout = 0   # большая база может копироваться долго
        foreach ($k in $params.Keys) { [void]$cmd.Parameters.AddWithValue($k, $params[$k]) }
        return $cmd.ExecuteScalar()
    } finally {
        $conn.Dispose()
    }
}

function Explain($err) {
    $e = $err.Exception
    while ($e.InnerException) { $e = $e.InnerException }
    return $e.Message
}

# ── Ночной запуск (от SYSTEM) ────────────────────────────────────────────────
if ($RunBackup) {
    $cfg = Get-Content $ConfigFile -Raw -Encoding UTF8 | ConvertFrom-Json
    $log = Join-Path $cfg.BackupDir "backup.log"
    function Log($text) { Add-Content -Path $log -Value ("{0:yyyy-MM-dd HH:mm:ss} {1}" -f (Get-Date), $text) -Encoding UTF8 }
    try {
        $file = Join-Path $cfg.BackupDir ("{0}-{1:yyyy-MM-dd-HHmm}.bak" -f $cfg.Database, (Get-Date))
        Log "Копирую базу $($cfg.Database) в $file"
        Invoke-Sql $cfg.Instance "BACKUP DATABASE @db TO DISK = @file WITH CHECKSUM, INIT" @{ "@db" = $cfg.Database; "@file" = $file } | Out-Null
        Invoke-Sql $cfg.Instance "RESTORE VERIFYONLY FROM DISK = @file WITH CHECKSUM" @{ "@file" = $file } | Out-Null
        $size = [math]::Round((Get-Item $file).Length / 1MB, 1)
        Log "Готово и проверено (RESTORE VERIFYONLY): $size МБ"

        $old = @(Get-ChildItem $cfg.BackupDir -Filter "$($cfg.Database)-*.bak" |
            Sort-Object LastWriteTime -Descending | Select-Object -Skip $cfg.Keep)
        foreach ($f in $old) { Remove-Item $f.FullName -Force; Log "Удалена старая копия $($f.Name)" }
    } catch {
        Log "ОШИБКА: $(Explain $_)"
        exit 1
    }
    exit 0
}

# ── Включение, выключение, запуск сейчас (от администратора) ────────────────
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { throw "Запустите PowerShell от имени администратора." }

if ($Unregister) {
    if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
    }
    Write-Host "Ночные копии выключены. Уже сделанные копии остались на месте." -ForegroundColor Green
    return
}

if ($RunNow) {
    if (-not (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue)) { throw "Сначала включите копии: -Register." }
    Start-ScheduledTask -TaskName $TaskName
    $cfg = Get-Content $ConfigFile -Raw -Encoding UTF8 | ConvertFrom-Json
    Write-Host "Копия запущена. Результат — в $(Join-Path $cfg.BackupDir 'backup.log') через минуту-другую." -ForegroundColor Green
    return
}

if (-not $Register) {
    Write-Host "Укажите -Register (включить), -RunNow (копия сейчас) или -Unregister (выключить)."
    return
}

Write-Host "Проверяю SQL Server $Instance и базу $Database..."
try {
    $exists = Invoke-Sql $Instance "SELECT COUNT(*) FROM sys.databases WHERE name = @db" @{ "@db" = $Database }
} catch {
    throw "Не удалось подключиться к SQL Server «$Instance» входом Windows: $(Explain $_)"
}
if ([int]$exists -ne 1) { throw "На сервере $Instance нет базы $Database." }

# Задача работает от SYSTEM. Имя этой учётной записи зависит от языка Windows, поэтому берём его по SID.
$systemName = (New-Object Security.Principal.SecurityIdentifier("S-1-5-18")).Translate([Security.Principal.NTAccount]).Value
# Право делать копии этой базы (db_backupoperator) и проверять их (RESTORE VERIFYONLY требует CREATE ANY DATABASE).
# Имена подставляются через QUOTENAME — без риска сломать запрос кавычками.
$grant = @'
DECLARE @sql nvarchar(max);
IF SUSER_ID(@login) IS NULL
    EXEC (N'CREATE LOGIN ' + QUOTENAME(@login) + N' FROM WINDOWS');
EXEC (N'GRANT CREATE ANY DATABASE TO ' + QUOTENAME(@login));
SET @sql = N'USE ' + QUOTENAME(@db) + N'; '
    + N'IF USER_ID(@login) IS NULL EXEC (N''CREATE USER '' + QUOTENAME(@login) + N'' FOR LOGIN '' + QUOTENAME(@login)); '
    + N'EXEC (N''ALTER ROLE db_backupoperator ADD MEMBER '' + QUOTENAME(@login));';
EXEC sp_executesql @sql, N'@login sysname', @login = @login;
SELECT 1;
'@
Invoke-Sql $Instance $grant @{ "@login" = $systemName; "@db" = $Database } | Out-Null

# Файл копии пишет служба SQL Server от своей учётной записи — даём ей право на папку.
New-Item -ItemType Directory -Force -Path $BackupDir | Out-Null
$serviceAccount = Invoke-Sql $Instance "SELECT TOP 1 service_account FROM sys.dm_server_services WHERE servicename LIKE N'SQL Server (%'"
if ($serviceAccount) {
    & icacls.exe $BackupDir /grant "${serviceAccount}:(OI)(CI)M" /C /Q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Не удалось дать службе SQL Server ($serviceAccount) право на папку $BackupDir." }
}

New-Item -ItemType Directory -Force -Path $Root | Out-Null
# Папку со скриптом меняют только SYSTEM и администраторы: из неё задача запускает скрипт с полными правами.
& icacls.exe $Root /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" /C /Q | Out-Null
Copy-Item $PSCommandPath (Join-Path $Root "backup-setup.ps1") -Force
[ordered]@{ Instance = $Instance; Database = $Database; BackupDir = (Resolve-Path $BackupDir).Path; Keep = $Keep } |
    ConvertTo-Json | Set-Content $ConfigFile -Encoding UTF8

$action = New-ScheduledTaskAction -Execute "powershell.exe" `
    -Argument "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$Root\backup-setup.ps1`" -RunBackup"
$trigger = New-ScheduledTaskTrigger -Daily -At $At
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit (New-TimeSpan -Hours 3) -MultipleInstances IgnoreNew
$principal = New-ScheduledTaskPrincipal -UserId "SYSTEM" -LogonType ServiceAccount -RunLevel Highest
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Principal $principal `
    -Description "Резервная копия базы $Database ($Instance) в $BackupDir, хранится $Keep последних" -Force | Out-Null

Write-Host "Ночные копии включены: каждый день в $At, папка $BackupDir, хранится $Keep последних." -ForegroundColor Green
Write-Host "Проверить сейчас:  powershell -ExecutionPolicy Bypass -File deploy\db\backup-setup.ps1 -RunNow"
Write-Host "Пропущенный запуск (компьютер был выключен) задача сделает при следующем включении."
