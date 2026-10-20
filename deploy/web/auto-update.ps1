# Автообновление сайта на демо-компьютере: новые коммиты на GitHub сами собираются и ставятся, без участия человека.
#
# Включить (один раз, от имени администратора; на компьютере нужны Git и .NET 10 SDK):
#   powershell -ExecutionPolicy Bypass -File deploy\web\auto-update.ps1 -Register [-Branch master] [-Port 8080]
# Выключить:
#   powershell -ExecutionPolicy Bypass -File deploy\web\auto-update.ps1 -Unregister
#
# Как работает: задача планировщика «ClinicHub Web Auto-Update» от SYSTEM при включении компьютера и раз в 5 минут
# запускает копию этого скрипта из C:\ClinicHub\Updater. Скрипт держит свой клон репозитория (C:\ClinicHub\Updater\src),
# смотрит ветку на GitHub и, если в ней новый коммит, собирает сайт (build-package.ps1 -NoZip) и ставит его
# (install.ps1 -Unattended). Подключение к базе, журналы и site-content.json установщик сохраняет.
# Журнал: C:\ClinicHub\Updater\logs. Если сборка коммита не удалась, этот коммит больше не пробуем — ждём следующего.
#
# Ветка: по умолчанию master. Если следим за другой веткой и её приняли в master (или удалили на GitHub),
# скрипт сам переключается на master.

param(
    [switch]$Register,
    [switch]$Unregister,
    [string]$Branch = "master",
    [int]$Port = 8080
)

$ErrorActionPreference = "Stop"
$Root = "C:\ClinicHub\Updater"
$Src = Join-Path $Root "src"
$LogDir = Join-Path $Root "logs"
$BranchFile = Join-Path $Root "branch.txt"
$PortFile = Join-Path $Root "port.txt"
$DeployedFile = Join-Path $Root "deployed-commit.txt"
$FailedFile = Join-Path $Root "failed-commit.txt"
$RepoUrl = "https://github.com/Regenika/ClinicHub.git"
$TaskName = "ClinicHub Web Auto-Update"
$ServiceName = "ClinicHub Web"

# ── Включение и выключение ───────────────────────────────────────────────────
if ($Register -or $Unregister) {
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) { throw "Запустите от имени администратора." }

    if ($Unregister) {
        if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
            Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
        }
        Write-Host "Автообновление выключено. Сайт остаётся установленным; папку $Root можно удалить." -ForegroundColor Green
        return
    }

    if (-not (Get-Command git.exe -ErrorAction SilentlyContinue)) { throw "Нужен Git for Windows." }
    if (-not ((& dotnet.exe --list-sdks 2>$null) -match "^10\.")) { throw "Нужен .NET 10 SDK." }

    New-Item -ItemType Directory -Force -Path $Root, $LogDir | Out-Null
    # Папку меняет только SYSTEM и администраторы: из неё задача запускает скрипт с полными правами.
    & icacls.exe $Root /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" /C /Q | Out-Null
    Copy-Item $PSCommandPath (Join-Path $Root "auto-update.ps1") -Force
    Set-Content $BranchFile $Branch -Encoding ASCII
    Set-Content $PortFile $Port -Encoding ASCII

    $action = New-ScheduledTaskAction -Execute "powershell.exe" `
        -Argument "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$Root\auto-update.ps1`""
    $every5min = New-ScheduledTaskTrigger -Once -At (Get-Date) -RepetitionInterval (New-TimeSpan -Minutes 5)
    $atStartup = New-ScheduledTaskTrigger -AtStartup
    $settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Hours 1) `
        -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    $principal = New-ScheduledTaskPrincipal -UserId "SYSTEM" -LogonType ServiceAccount -RunLevel Highest
    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $every5min, $atStartup -Settings $settings `
        -Principal $principal -Description "Сборка и установка новой версии сайта ClinicHub.Web с GitHub ($RepoUrl)" -Force | Out-Null
    Start-ScheduledTask -TaskName $TaskName
    Write-Host "Автообновление включено: ветка $Branch, проверка раз в 5 минут. Журнал: $LogDir" -ForegroundColor Green
    return
}

# ── Проверка и обновление (запускает задача от SYSTEM) ───────────────────────
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
$log = Join-Path $LogDir ("update-{0:yyyy-MM-dd}.log" -f (Get-Date))
function Log($text) { Add-Content -Path $log -Value ("{0:yyyy-MM-dd HH:mm:ss} {1}" -f (Get-Date), $text) -Encoding UTF8 }

Get-ChildItem $LogDir -Filter "update-*.log" | Where-Object LastWriteTime -lt (Get-Date).AddDays(-30) | Remove-Item -Force

# Сборка от SYSTEM: свои папки для .NET и пакетов NuGet, а не профиль системы.
$env:DOTNET_CLI_HOME = $Root
$env:NUGET_PACKAGES = Join-Path $Root "nuget"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"
$env:GIT_TERMINAL_PROMPT = "0"

function Git {
    $out = & git.exe -C $Src -c "safe.directory=$($Src.Replace('\', '/'))" @args 2>&1
    if ($LASTEXITCODE -ne 0) { throw "git $($args -join ' '): $out" }
    $out
}

# Дочерний PowerShell с выводом в журнал. Установщик завершается через exit, поэтому не в этом процессе.
function Run-Script([string]$file, [string[]]$scriptArgs) {
    $outFile = Join-Path $Root "step-output.txt"
    $argLine = @("-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command",
        "`"[Console]::OutputEncoding = [Text.Encoding]::UTF8; & '$file' $($scriptArgs -join ' '); exit `$LASTEXITCODE`"")
    $p = Start-Process powershell.exe -ArgumentList $argLine -Wait -PassThru -NoNewWindow `
        -RedirectStandardOutput $outFile -RedirectStandardError "$outFile.err"
    foreach ($f in $outFile, "$outFile.err") {
        if (Test-Path $f) { Get-Content $f -Encoding UTF8 | Where-Object { $_.Trim() } | ForEach-Object { Log "    $_" } }
    }
    return $p.ExitCode
}

try {
    $branch = if (Test-Path $BranchFile) { (Get-Content $BranchFile -Raw).Trim() } else { "master" }
    $port = if (Test-Path $PortFile) { [int](Get-Content $PortFile -Raw).Trim() } else { 8080 }

    if (-not (Test-Path (Join-Path $Src ".git"))) {
        Log "Клонирую $RepoUrl"
        $out = & git.exe clone --quiet $RepoUrl $Src 2>&1
        if ($LASTEXITCODE -ne 0) { throw "git clone: $out" }
    }
    Git fetch --quiet --prune origin | Out-Null

    if ($branch -ne "master") {
        $exists = [bool](Git branch -r --list "origin/$branch")
        if (-not $exists) {
            Log "Ветки $branch на GitHub больше нет — дальше слежу за master."
            $branch = "master"
        } else {
            & git.exe -C $Src -c "safe.directory=$($Src.Replace('\', '/'))" merge-base --is-ancestor "origin/$branch" origin/master
            if ($LASTEXITCODE -eq 0) {
                Log "Ветка $branch уже в master — дальше слежу за master."
                $branch = "master"
            }
        }
        if ($branch -eq "master") { Set-Content $BranchFile $branch -Encoding ASCII }
    }

    $head = (Git rev-parse "origin/$branch" | Select-Object -First 1).ToString().Trim()
    $deployed = if (Test-Path $DeployedFile) { (Get-Content $DeployedFile -Raw).Trim() } else { "" }
    $failed = if (Test-Path $FailedFile) { (Get-Content $FailedFile -Raw).Trim() } else { "" }
    if ($head -eq $deployed -or $head -eq $failed) { return }

    $subject = (Git log -1 --format=%s $head | Select-Object -First 1)
    Log "Новая версия в ${branch}: $($head.Substring(0, 7)) $subject"
    Git checkout --quiet --force --detach $head | Out-Null
    Git clean -fdq -e artifacts | Out-Null

    Log "Собираю сайт"
    if ((Run-Script (Join-Path $Src "deploy\web\build-package.ps1") @("-NoZip")) -ne 0) {
        throw "сборка не удалась (подробности выше)"
    }

    Log "Устанавливаю"
    if ((Run-Script (Join-Path $Src "artifacts\web-package\install.ps1") @("-Port", $port, "-Unattended")) -ne 0) {
        throw "установка не удалась (подробности выше)"
    }

    Set-Content $DeployedFile $head -Encoding ASCII
    Remove-Item $FailedFile -ErrorAction SilentlyContinue
    # Следующий запуск — новой версией этого скрипта из того же коммита.
    $newScript = Join-Path $Src "deploy\web\auto-update.ps1"
    if (Test-Path $newScript) { Copy-Item $newScript (Join-Path $Root "auto-update.ps1") -Force }
    Log "Готово: установлена $($head.Substring(0, 7))"
} catch {
    Log "ОШИБКА: $($_.Exception.Message)"
    if ($head) {
        Set-Content $FailedFile $head -Encoding ASCII
        # Сайт мог остаться остановленным посреди установки — пусть работает прежняя версия, если файлы целы.
        $svc = Get-Service $ServiceName -ErrorAction SilentlyContinue
        if ($svc -and $svc.Status -ne "Running") { Start-Service $ServiceName -ErrorAction SilentlyContinue }
    }
    exit 1
}
