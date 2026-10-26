# Проверка модуля согласий (Issue #34) на запущенном сервере.
# Запуск (сервер должен работать):
#   powershell -ExecutionPolicy Bypass -File tools\smoke-consents.ps1
#   powershell -ExecutionPolicy Bypass -File tools\smoke-consents.ps1 -BaseUrl http://localhost:5112 -Login admin
# Пароль спросит сам. Пациенты вымышленные и ОТПИСАНЫ от рассылок — ни одна SMS не уйдёт, даже с подключённым
# телефоном-шлюзом. Отправку с согласием проверяют модульные тесты (ClinicHub.Api.Tests). Пациенты в конце удаляются;
# тестовая заявка остаётся в истории (заявки не удаляются).

param(
    [string]$BaseUrl = "http://localhost:5112",
    [string]$Login = "admin",
    [string]$Password,
    # Ключ вебхука сайта (AppSettings «WebhookApiKey»). Не задан — ищем в журнале сервера, нет и там — шаг сайта пропускаем.
    [string]$WebhookKey
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [Text.Encoding]::UTF8
if (-not $Password) {
    $secure = Read-Host "Пароль пользователя $Login" -AsSecureString
    $Password = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure))
}

$script:token = $null
$script:failed = 0
$script:passed = 0

# Запрос с проверкой кода ответа. Возвращает разобранный JSON (или $null).
function Call([string]$Method, [string]$Path, $Body = $null, [int]$Expect = 200, [switch]$Anon, [string]$Title = "", [hashtable]$Headers = @{}) {
    $headers = @{} + $Headers
    if ($script:token -and -not $Anon) { $headers["Authorization"] = "Bearer $($script:token)" }
    $params = @{ Method = $Method; Uri = "$BaseUrl$Path"; Headers = $headers; UseBasicParsing = $true }
    if ($null -ne $Body) {
        $json = $Body | ConvertTo-Json -Depth 6 -Compress
        $params["Body"] = [Text.Encoding]::UTF8.GetBytes($json)
        $params["ContentType"] = "application/json; charset=utf-8"
    }
    $status = 0; $content = ""
    try {
        $resp = Invoke-WebRequest @params
        $status = [int]$resp.StatusCode
        $content = [Text.Encoding]::UTF8.GetString($resp.RawContentStream.ToArray())
    } catch [System.Net.WebException] {
        $r = $_.Exception.Response
        if ($null -eq $r) { throw }
        $status = [int]$r.StatusCode
        $reader = New-Object IO.StreamReader($r.GetResponseStream(), [Text.Encoding]::UTF8)
        $content = $reader.ReadToEnd()
    }
    $label = if ($Title) { $Title } else { "$Method $Path" }
    if ($status -eq $Expect) {
        $script:passed++
        Write-Host ("OK    {0} -> {1}" -f $label, $status)
    } else {
        $script:failed++
        Write-Host ("FAIL  {0} -> {1}, ожидалось {2}. Ответ: {3}" -f $label, $status, $Expect, $content) -ForegroundColor Red
    }
    if ($content -and ($content.TrimStart().StartsWith("{") -or $content.TrimStart().StartsWith("["))) {
        # Windows PowerShell 5.1: «return ... | ConvertFrom-Json» отдаёт JSON-массив одним объектом, и @(Call ...)
        # превращается в массив из одного массива. Через переменную return разворачивает его как положено.
        $parsed = $content | ConvertFrom-Json
        return $parsed
    }
    return $null
}

function Check([bool]$Condition, [string]$Text) {
    if ($Condition) { $script:passed++; Write-Host "OK    $Text" }
    else { $script:failed++; Write-Host "FAIL  $Text" -ForegroundColor Red }
}

# Пробелы в «4 000» сервер ставит неразрывные: для сравнения приводим к обычным.
function Norm([string]$Text) { return ($Text -replace '\s', ' ') }

Write-Host "== Вход"
$auth = Call POST "/api/auth/login" @{ login = $Login; password = $Password } -Anon -Title "Вход"
if (-not $auth) { Write-Host "Не удалось войти — дальше проверять нечего." -ForegroundColor Red; exit 1 }
$script:token = $auth.accessToken

$stamp = Get-Date -Format "HHmmss"
# Оба пациента ОТПИСАНЫ: даже если подключён телефон-шлюз, ни одна SMS не уйдёт. Номера вымышленные.
$p = Call POST "/api/patients" @{ fullName = "Тестов Согласие $stamp"; phone = "+7999003$($stamp.Substring(2))"; isSubscribed = $false; notes = "smoke-тест согласий" } -Expect 201 -Title "пациент для теста (отписан)"

Write-Host "== Журнал и текущее состояние"
$c = Call GET "/api/patients/$($p.id)/consents" -Title "согласия нового пациента"
Check (@($c.journal).Count -eq 0) "журнал пуст"
$smsMk = $c.current | Where-Object { $_.purpose -eq "Marketing" -and $_.channel -eq "Sms" }
Check ($smsMk.state -eq "none" -and -not $smsMk.canSend) "реклама по SMS: записей нет, отправлять нельзя"

$g = Call POST "/api/patients/$($p.id)/consents" @{ purpose = "Marketing"; channel = "Sms"; granted = $true; source = "PaperForm"; evidenceRef = "форма smoke-$stamp" } -Expect 201 -Title "внести бумажную форму: согласие на рекламу по SMS"
Check ($g.byLogin -eq $Login -and $g.evidenceRef -eq "форма smoke-$stamp") "в записи — кто внёс и номер формы"
$c = Call GET "/api/patients/$($p.id)/consents" -Title "состояние после согласия"
$smsMk = $c.current | Where-Object { $_.purpose -eq "Marketing" -and $_.channel -eq "Sms" }
$tgMk = $c.current | Where-Object { $_.purpose -eq "Marketing" -and $_.channel -eq "Telegram" }
Check ($smsMk.state -eq "granted" -and $tgMk.state -eq "none") "SMS — согласие, Telegram — нет записей"
Check (-not $smsMk.canSend -and $smsMk.reason -like "*отписан*") "пациент отписан — всё равно нельзя: $($smsMk.reason)"

Call POST "/api/patients/$($p.id)/consents" @{ purpose = "Marketing"; channel = "Any"; granted = $false; source = "Verbal"; comment = "попросил не присылать рекламу" } -Expect 201 -Title "отзыв на все каналы" | Out-Null
$c = Call GET "/api/patients/$($p.id)/consents" -Title "состояние после отзыва"
$smsMk = $c.current | Where-Object { $_.purpose -eq "Marketing" -and $_.channel -eq "Sms" }
Check ($smsMk.state -eq "revoked") "отзыв на все каналы перекрыл согласие по SMS"
Check (@($c.journal).Count -eq 2) "в журнале обе записи — ничего не перезаписано"

Write-Host "== Ошибки ввода"
Call POST "/api/patients/$($p.id)/consents" @{ purpose = "Marketing"; channel = "Sms"; granted = $true; source = "Import" } -Expect 400 -Title "импорт не даёт согласия на рекламу -> 400" | Out-Null
Call POST "/api/patients/$($p.id)/consents" @{ channel = "Sms"; granted = $true; source = "PaperForm" } -Expect 400 -Title "без purpose -> 400" | Out-Null
Call POST "/api/patients/$($p.id)/consents" @{ purpose = "Marketing"; channel = "Sms"; granted = $true; source = "PaperForm"; at = (Get-Date).ToUniversalTime().AddDays(2).ToString("o") } -Expect 400 -Title "согласие из будущего -> 400" | Out-Null
Call PUT "/api/patients/$($p.id)/consents" @{ purpose = "Marketing" } -Expect 405 -Title "изменять журнал нельзя (нет метода) -> 405" | Out-Null
Call GET "/api/patients/999999/consents" -Expect 404 -Title "нет пациента -> 404" | Out-Null

Write-Host "== Цель у заявок"
$base = @{ type = "Personal"; title = "smoke согласий $stamp"; sendMode = "Now"; textTemplate = "Тест, не отправляется"; recipients = @(@{ patientId = $p.id }) }
Call POST "/api/requests" $base -Expect 400 -Title "заявка без purpose -> 400" | Out-Null
$base["purpose"] = "Marketing"
$req = Call POST "/api/requests" $base -Expect 201 -Title "рекламная заявка создана"
Check ($req.purpose -eq "Marketing") "у заявки цель Marketing"
Call POST "/api/requests/$($req.id)/approve" -Title "одобрить (пациент отписан — в очередь не попадёт)" | Out-Null
$deadline = (Get-Date).AddSeconds(30)
do {
    Start-Sleep -Seconds 3
    $full = Call GET "/api/requests/$($req.id)" -Title "ожидание планировщика"
    $rec = @($full.recipients)[0]
} while ($rec.status -ne "Skipped" -and (Get-Date) -lt $deadline)
Check ($rec.status -eq "Skipped" -and $rec.skipReason -like "*отписан*") "получатель пропущен с причиной: $($rec.skipReason)"
Check ($full.status -eq "Failed" -and $full.skippedCount -eq 1) "заявка завершена: никому не отправлено, пропущено 1"

Write-Host "== Согласие с сайта"
if (-not $WebhookKey) {
    $log = Join-Path $PSScriptRoot "..\ClinicHub.Api\logs\clinichub.log"
    if (Test-Path $log) {
        $m = Select-String -Path $log -Pattern "Webhook API-ключ для сайта клиники: (\S+)" | Select-Object -Last 1
        if ($m) { $WebhookKey = $m.Matches[0].Groups[1].Value }
    }
}
if (-not $WebhookKey) {
    Write-Host "      ключ вебхука не найден (параметр -WebhookKey или журнал сервера) — шаг пропускаем"
} else {
    $site = @{ phone = "8 (999) 004-$($stamp.Substring(2,2))-$($stamp.Substring(4,2))"; name = "Тестов Сайт $stamp"; channels = @("Sms", "Telegram"); evidenceRef = "booking-smoke-$stamp" }
    Call POST "/api/consents/site" $site -Anon -Expect 401 -Title "без ключа -> 401" | Out-Null
    $s1 = Call POST "/api/consents/site" $site -Anon -Headers @{ "X-Webhook-Api-Key" = $WebhookKey } -Title "согласие с сайта: новый пациент"
    Check ($s1.patientCreated -and @($s1.records).Count -eq 2) "пациент создан, записей две (SMS и Telegram)"
    $s2 = Call POST "/api/consents/site" $site -Anon -Headers @{ "X-Webhook-Api-Key" = $WebhookKey } -Title "повтор с тем же телефоном"
    Check (-not $s2.patientCreated -and $s2.patientId -eq $s1.patientId) "пациент найден по телефону, второй не создан"
    $site["phone"] = "12345"
    Call POST "/api/consents/site" $site -Anon -Headers @{ "X-Webhook-Api-Key" = $WebhookKey } -Expect 400 -Title "плохой телефон -> 400" | Out-Null
    Call DELETE "/api/patients/$($s1.patientId)" -Expect 204 -Title "уборка: пациент с сайта" | Out-Null
}

Write-Host "== Уборка"
Call DELETE "/api/patients/$($p.id)" -Expect 204 -Title "пациент удаляется вместе с журналом согласий" | Out-Null

Write-Host ""
if ($script:failed -eq 0) {
    Write-Host "Итого: все проверки прошли ($($script:passed))." -ForegroundColor Green
} else {
    Write-Host "Итого: прошло $($script:passed), упало $($script:failed)." -ForegroundColor Red
    exit 1
}
