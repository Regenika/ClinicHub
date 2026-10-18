# Проверка API каталога и визитов (Issue #13) на запущенном сервере.
# Запуск (сервер должен работать):
#   powershell -ExecutionPolicy Bypass -File tools\smoke-catalog-visits.ps1
#   powershell -ExecutionPolicy Bypass -File tools\smoke-catalog-visits.ps1 -BaseUrl http://localhost:5112 -Login admin
# Пароль спросит сам. Создаёт вымышленные данные с префиксом «test-» и удаляет их в конце.

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
    if ($content -and $content.TrimStart().StartsWith("{") -or $content.TrimStart().StartsWith("[")) {
        return $content | ConvertFrom-Json
    }
    return $null
}

function Check([bool]$Condition, [string]$Text) {
    if ($Condition) { $script:passed++; Write-Host "OK    $Text" }
    else { $script:failed++; Write-Host "FAIL  $Text" -ForegroundColor Red }
}

Write-Host "== Вход"
$auth = Call POST "/api/auth/login" @{ login = $Login; password = $Password } -Anon -Title "Вход"
if (-not $auth) { Write-Host "Не удалось войти — дальше проверять нечего." -ForegroundColor Red; exit 1 }
$script:token = $auth.accessToken

Write-Host "== Каталог для сайта (без входа)"
$pubServices = Call GET "/api/catalog/services" -Anon
Check ($pubServices.Count -ge 1) "на сайте есть услуги (стартовые данные): $($pubServices.Count)"
$pubDoctors = Call GET "/api/catalog/doctors" -Anon
Check ($pubDoctors.Count -ge 1) "на сайте есть врачи: $($pubDoctors.Count)"
Call GET "/api/catalog/categories" -Anon | Out-Null

Write-Host "== Каталог: админка"
$cat = Call POST "/api/catalog/admin/categories" @{ name = "test-категория"; sortOrder = 10; isVisibleOnSite = $true } -Expect 201
Call PUT "/api/catalog/admin/categories/$($cat.id)" @{ name = "test-категория (правка)"; sortOrder = 10; isVisibleOnSite = $true } | Out-Null
Call GET "/api/catalog/admin/categories" | Out-Null
Call GET "/api/catalog/admin/services" | Out-Null

$svc = @{ id = "test-botox"; categoryId = $cat.id; name = "test: ботулинотерапия"; description = "проверка"; priceFrom = 3500; durationMinutes = 30; isVisibleOnSite = $false; sortOrder = 100 }
Call POST "/api/catalog/admin/services" $svc -Expect 201 | Out-Null
Call POST "/api/catalog/admin/services" $svc -Expect 409 -Title "повторный id услуги -> 409" | Out-Null
$svc.Remove("id"); $svc.priceFrom = 4000
$updated = Call PUT "/api/catalog/admin/services/test-botox" $svc
Check ($updated.priceFrom -eq 4000) "цена обновилась до 4000"
$history = Call GET "/api/catalog/admin/services/test-botox/price-history"
# История цен переживает удаление услуги, поэтому после прошлых прогонов там могут быть старые записи:
# проверяем две последние (сервер отдаёт свежие сверху).
$h = @($history)
Check ($h.Count -ge 2 -and $h[0].oldPrice -eq 3500 -and $h[0].newPrice -eq 4000 -and $null -eq $h[1].oldPrice -and $h[1].newPrice -eq 3500) "в истории цен последние записи: создание (— -> 3500) и правка (3500 -> 4000)"
Check ((@($pubServices) | Where-Object { $_.id -eq "test-botox" }).Count -eq 0) "скрытая услуга не видна на сайте"
Call POST "/api/catalog/admin/services" @{ id = "Ботокс 1"; name = "плохой id"; durationMinutes = 30 } -Expect 400 -Title "неправильный id услуги -> 400" | Out-Null
Call POST "/api/catalog/admin/services" @{ id = "test-bad-price"; name = "x"; priceFrom = -1; durationMinutes = 30 } -Expect 400 -Title "отрицательная цена -> 400" | Out-Null
Call PUT "/api/catalog/admin/services/test-botox" @{ name = "без входа"; durationMinutes = 30 } -Anon -Expect 401 -Title "правка без входа -> 401" | Out-Null

$doc = Call POST "/api/catalog/admin/doctors" @{ id = "test-doctor"; name = "Тестова Анна Петровна"; role = "Врач-косметолог"; serviceIds = @("consultation", "test-botox"); isVisibleOnSite = $false; sortOrder = 100 } -Expect 201
Check (@($doc.serviceIds).Count -eq 2) "у врача 2 услуги"
$doc = Call PUT "/api/catalog/admin/doctors/test-doctor" @{ name = "Тестова Анна Петровна"; role = "Врач-косметолог"; serviceIds = @("consultation", "test-botox", "facial-care"); isVisibleOnSite = $false; sortOrder = 100 }
Check (@($doc.serviceIds).Count -eq 3) "список услуг врача заменился, теперь 3"
Call POST "/api/catalog/admin/doctors" @{ id = "test-doctor-2"; name = "x"; serviceIds = @("no-such-service") } -Expect 400 -Title "врач с несуществующей услугой -> 400" | Out-Null

Write-Host "== Визиты"
$patient = Call POST "/api/patients" @{ fullName = "Тестов Тест Тестович"; phone = "+79990000001"; isSubscribed = $true; notes = "smoke-тест" } -Expect 201
$now = (Get-Date).ToUniversalTime()

$v1 = Call POST "/api/visits" @{ patientId = $patient.id; startAt = $now.AddHours(-1).ToString("o"); doctorId = "test-doctor"; serviceIds = @("consultation"); comment = "тест" } -Expect 201
Check ($v1.status -eq "Planned" -and $v1.doctorName -eq "Тестова Анна Петровна") "новый визит Planned, имя врача подставилось из каталога"
$v1 = Call POST "/api/visits/$($v1.id)/complete" @{ serviceIds = @("consultation", "test-botox"); comment = "всё хорошо" } -Title "визит состоялся"
Check ($v1.status -eq "Completed" -and @($v1.services).Count -eq 2 -and $v1.confirmedAt) "статус Completed, 2 процедуры, есть время подтверждения"
Call POST "/api/visits/$($v1.id)/complete" @{} -Expect 409 -Title "повторное подтверждение -> 409" | Out-Null
$p = Call GET "/api/patients/$($patient.id)"
Check ($null -ne $p.lastVisitAt) "у пациента заполнился lastVisitAt: $($p.lastVisitAt)"

$v2 = Call POST "/api/visits" @{ patientId = $patient.id; startAt = $now.AddDays(1).ToString("o"); doctorName = "Врач не из каталога"; serviceIds = @("facial-care") } -Expect 201
Call POST "/api/visits/$($v2.id)/complete" @{} -Expect 400 -Title "будущий визит нельзя отметить состоявшимся -> 400" | Out-Null
$v2 = Call POST "/api/visits/$($v2.id)/cancel" @{ comment = "перенёс" } -Title "отмена записи"
Check ($v2.status -eq "Cancelled") "статус Cancelled"
Call POST "/api/visits/$($v2.id)/no-show" @{} -Expect 409 -Title "неявка по отменённому визиту -> 409" | Out-Null

$v3 = Call POST "/api/visits" @{ patientId = $patient.id; startAt = $now.AddHours(-2).ToString("o"); serviceIds = @() } -Expect 201
$v3 = Call POST "/api/visits/$($v3.id)/no-show" $null -Title "неявка (без тела запроса)"
Check ($v3.status -eq "NoShow") "статус NoShow"

$list = Call GET "/api/visits?patientId=$($patient.id)"
Check (@($list).Count -eq 3) "у пациента 3 визита: $(@($list).Count)"
$today = (Get-Date).ToString("yyyy-MM-dd")
$day = Call GET "/api/visits?date=$today"
Check ((@($day) | Where-Object { $_.patientId -eq $patient.id }).Count -ge 2) "визиты за сегодня по Москве включают тестовые"
Call GET "/api/visits?date=$today&status=Planned" | Out-Null
Call GET "/api/visits" -Expect 400 -Title "список без patientId и date -> 400" | Out-Null
Call POST "/api/visits" @{ patientId = 999999999; startAt = $now.ToString("o") } -Expect 400 -Title "визит несуществующего пациента -> 400" | Out-Null

Write-Host "== Сопоставление внешних услуг"
$map = Call PUT "/api/visits/service-map" @{ source = "Dikidi"; externalKey = "test: Ботокс"; serviceId = "test-botox" }
$map2 = Call PUT "/api/visits/service-map" @{ source = "Dikidi"; externalKey = "test: Ботокс"; serviceId = "consultation" } -Title "повторный PUT того же ключа меняет услугу"
Check ($map2.id -eq $map.id -and $map2.serviceId -eq "consultation") "сопоставление обновилось, а не задвоилось"
Call GET "/api/visits/service-map?source=Dikidi" | Out-Null
Call DELETE "/api/catalog/admin/services/test-botox" -Expect 409 -Title "услугу из визитов удалить нельзя -> 409" | Out-Null

Write-Host "== Уборка"
Call DELETE "/api/visits/service-map/$($map.id)" -Expect 204 | Out-Null
Call DELETE "/api/patients/$($patient.id)" -Expect 204 -Title "пациент удаляется вместе с визитами" | Out-Null
Call DELETE "/api/catalog/admin/services/test-botox" -Expect 204 -Title "теперь услугу можно удалить" | Out-Null
Call DELETE "/api/catalog/admin/doctors/test-doctor" -Expect 204 | Out-Null
Call DELETE "/api/catalog/admin/categories/$($cat.id)" -Expect 204 | Out-Null

Write-Host ""
if ($script:failed -eq 0) {
    Write-Host "Итого: все проверки прошли ($($script:passed))." -ForegroundColor Green
} else {
    Write-Host "Итого: прошло $($script:passed), упало $($script:failed)." -ForegroundColor Red
    exit 1
}
