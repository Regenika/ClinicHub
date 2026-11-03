# Проверка сценариев напоминаний (Issue #14) на запущенном сервере.
# Запуск (сервер должен работать):
#   powershell -ExecutionPolicy Bypass -File tools\smoke-scenarios.ps1
#   powershell -ExecutionPolicy Bypass -File tools\smoke-scenarios.ps1 -BaseUrl http://localhost:5112 -Login admin
# Пароль спросит сам. Создаёт вымышленного пациента, ОТПИСАННОГО от рассылок, поэтому ни одна SMS не уйдёт,
# даже если подключён телефон-шлюз. Настоящую передачу в отправку проверяют модульные тесты (ClinicHub.Api.Tests).
# В конце всё созданное удаляется.

param(
    [string]$BaseUrl = "http://localhost:5112",
    [string]$Login = "admin",
    [string]$Password
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
function Call([string]$Method, [string]$Path, $Body = $null, [int]$Expect = 200, [switch]$Anon, [string]$Title = "") {
    $headers = @{}
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

# Время по Москве для проверки сроков (компьютер может стоять в другом поясе).
$msk = [TimeZoneInfo]::FindSystemTimeZoneById("Russian Standard Time")
function MskDate($utcText) { return [TimeZoneInfo]::ConvertTimeFromUtc(([DateTime]::Parse($utcText)).ToUniversalTime(), $msk) }
$nowMsk = [TimeZoneInfo]::ConvertTimeFromUtc((Get-Date).ToUniversalTime(), $msk)
# Окно отправки — из RateConfig (по умолчанию 9:00–20:00 МСК), то же, что у планировщика SMS.
$quiet = $nowMsk.TimeOfDay -lt [TimeSpan]"09:00" -or $nowMsk.TimeOfDay -gt [TimeSpan]"20:00"

Write-Host "== Сценарии"
$list = Call GET "/api/scenarios"
Check (@($list).Count -ge 2) "есть примеры сценариев: $(@($list).Count)"

$step = @{ order = 1; offsetDays = 14; sendTime = "11:00"; text = "{name}, пора на контрольный осмотр"; cancelIfFutureVisit = $true }
$sc = Call POST "/api/scenarios" @{ name = "test: контроль"; purpose = "Service"; trigger = "VisitCompleted"; triggerServiceIds = @("consultation"); steps = @($step) } -Expect 201
Check ($sc.steps[0].sendTime -eq "11:00" -and @($sc.triggerServiceIds).Count -eq 1) "сценарий создан: шаг на 11:00, услуга consultation"

$bad = @{ order = 1; offsetDays = 1; sendTime = "22:00"; text = "x" }
Call POST "/api/scenarios" @{ name = "test: ночь"; purpose = "Service"; trigger = "Manual"; steps = @($bad) } -Expect 400 -Title "время шага вне окна отправки -> 400" | Out-Null
Call POST "/api/scenarios" @{ name = "test: др"; purpose = "Service"; trigger = "Birthday"; steps = @($step) } -Expect 400 -Title "день рождения пока не поддерживается -> 400" | Out-Null
Call POST "/api/scenarios" @{ name = "test: давно"; purpose = "Marketing"; trigger = "NoVisitFor"; steps = @($step) } -Expect 400 -Title "«давно не были» без числа дней -> 400" | Out-Null
Call POST "/api/scenarios" @{ name = "test: нет услуги"; purpose = "Service"; trigger = "VisitCompleted"; triggerServiceIds = @("no-such"); steps = @($step) } -Expect 400 -Title "несуществующая услуга -> 400" | Out-Null
Call POST "/api/scenarios" @{ name = "test: без шагов"; purpose = "Service"; trigger = "Manual"; steps = @() } -Expect 400 -Title "без шагов -> 400" | Out-Null

Write-Host "== От визита к напоминанию"
$patient = Call POST "/api/patients" @{ fullName = "Тестов Сценарий"; phone = "+79990000002"; isSubscribed = $false; notes = "smoke-тест сценариев, отписан" } -Expect 201
$now = (Get-Date).ToUniversalTime()
$v1 = Call POST "/api/visits" @{ patientId = $patient.id; startAt = $now.AddHours(-1).ToString("o"); serviceIds = @("consultation") } -Expect 201

$preview = @(Call POST "/api/scenarios/preview" @{ visitId = $v1.id } -Title "предпросмотр")
$mine = @($preview | Where-Object { $_.scenarioId -eq $sc.id })
$expected = (MskDate $v1.startAt).Date.AddDays(14).AddHours(11)
Check ($mine.Count -eq 1 -and (MskDate $mine[0].dueAt) -eq $expected) "предпросмотр: напоминание на $($expected.ToString('dd.MM.yyyy HH:mm')) МСК"
Check ($mine[0].text -eq "Тестов Сценарий, пора на контрольный осмотр") "в тексте подставлено имя"

Call POST "/api/visits/$($v1.id)/complete" @{} -Title "визит состоялся" | Out-Null
$rem = @(Call GET "/api/reminders?patientId=$($patient.id)")
$r1 = @($rem | Where-Object { $_.scenarioId -eq $sc.id -and $_.status -eq "Planned" })
Check ($r1.Count -eq 1 -and $r1[0].triggerVisitId -eq $v1.id) "после подтверждения визита запланировано напоминание"

$v2 = Call POST "/api/visits" @{ patientId = $patient.id; startAt = $now.AddMinutes(-30).ToString("o"); serviceIds = @("consultation") } -Expect 201
Call POST "/api/visits/$($v2.id)/complete" @{} -Title "второй визит" | Out-Null
$rem = @(Call GET "/api/reminders?patientId=$($patient.id)")
$old = $rem | Where-Object { $_.id -eq $r1[0].id }
Check ($old.status -eq "Cancelled" -and $old.statusReason -like "*перезапущена*") "старая цепочка отменена: $($old.statusReason)"
$r2 = @($rem | Where-Object { $_.scenarioId -eq $sc.id -and $_.status -eq "Planned" })
Check ($r2.Count -eq 1 -and $r2[0].triggerVisitId -eq $v2.id) "новая цепочка от второго визита"

Write-Host "== Правка, ручные, отмена"
$edited = Call PUT "/api/reminders/$($r2[0].id)" @{ dueAt = $now.AddMinutes(-1).ToString("o"); text = "Тест: срок сдвинут в прошлое" } -Title "сдвинуть срок в прошлое и поправить текст"
Check ($edited.text -eq "Тест: срок сдвинут в прошлое") "текст поправлен"

$manual = Call POST "/api/reminders" @{ patientId = $patient.id; inDays = 3; time = "10:30"; text = "{name}, ручное напоминание" } -Expect 201 -Title "ручное через 3 дня в 10:30"
$mDue = MskDate $manual.dueAt
Check ($mDue.Date -eq $nowMsk.Date.AddDays(3) -and $mDue.Hour -eq 10 -and $mDue.Minute -eq 30) "ручное: $($mDue.ToString('dd.MM.yyyy HH:mm')) МСК"
Call POST "/api/reminders" @{ patientId = $patient.id; inDays = 1; time = "23:00"; text = "ночь" } -Expect 400 -Title "ручное вне окна отправки -> 400" | Out-Null
$c = Call POST "/api/reminders/$($manual.id)/cancel" -Title "отмена ручного"
Check ($c.status -eq "Cancelled") "ручное отменено"
Call POST "/api/reminders/$($manual.id)/cancel" -Expect 409 -Title "повторная отмена -> 409" | Out-Null
Call PUT "/api/reminders/$($manual.id)" @{ text = "x" } -Expect 409 -Title "правка отменённого -> 409" | Out-Null

$mk = Call POST "/api/scenarios" @{ name = "test: реклама"; purpose = "Marketing"; trigger = "Manual"; steps = @(@{ order = 1; offsetDays = 0; sendTime = "09:00"; text = "{name}, акция" }) } -Expect 201
$applied = @(Call POST "/api/scenarios/$($mk.id)/apply" @{ patientId = $patient.id } -Title "применить рекламный сценарий вручную")
Check ($applied.Count -eq 1 -and $applied[0].purpose -eq "Marketing") "рекламное напоминание создано"
Call PUT "/api/reminders/$($applied[0].id)" @{ dueAt = $now.AddMinutes(-1).ToString("o") } -Title "рекламное — срок в прошлое" | Out-Null

Write-Host "== Фоновая отправка (воркер раз в минуту)"
if ($quiet) {
    Write-Host "      сейчас вне окна отправки 9:00–20:00 МСК: воркер ничего не отправляет, проверку отправки пропускаем"
} else {
    $deadline = (Get-Date).AddSeconds(100)
    do {
        Start-Sleep -Seconds 5
        $rem = @(Call GET "/api/reminders?patientId=$($patient.id)" -Title "ожидание воркера")
        $a = $rem | Where-Object { $_.id -eq $r2[0].id }
        $b = $rem | Where-Object { $_.id -eq $applied[0].id }
    } while (($a.status -eq "Planned" -or $b.status -eq "Planned") -and (Get-Date) -lt $deadline)
    Check ($b.status -eq "Skipped" -and $b.statusReason -like "*отписан*") "рекламное пропущено (пациент отписан): $($b.statusReason)"
    Check ($a.status -eq "Skipped" -and $a.statusReason -like "*отписан*") "отписанному не отправлено: $($a.statusReason)"
    Check ($null -eq $a.requestId -and $null -eq $b.requestId) "заявок на отправку не создано"
}

Write-Host "== Уборка"
Call DELETE "/api/scenarios/$($sc.id)" -Expect 204 -Title "удалить сценарий" | Out-Null
Call DELETE "/api/scenarios/$($mk.id)" -Expect 204 -Title "удалить рекламный сценарий" | Out-Null
Call DELETE "/api/patients/$($patient.id)" -Expect 204 -Title "пациент удаляется вместе с визитами и напоминаниями" | Out-Null

Write-Host ""
if ($script:failed -eq 0) {
    Write-Host "Итого: все проверки прошли ($($script:passed))." -ForegroundColor Green
} else {
    Write-Host "Итого: прошло $($script:passed), упало $($script:failed)." -ForegroundColor Red
    exit 1
}
