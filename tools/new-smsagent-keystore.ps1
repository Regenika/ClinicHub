# Создать постоянный ключ подписи SmsAgent (#50). Запускается ОДИН раз за всю жизнь приложения.
#
#   cd C:\Users\<вы>\source\clinichub-review
#   powershell -ExecutionPolicy Bypass -File tools\new-smsagent-keystore.ps1
#
# Кладёт в папку ВНЕ репозитория (по умолчанию %USERPROFILE%\ClinicHub-keys):
#   smsagent.keystore              — сам ключ (PKCS12);
#   smsagent.keystore.base64.txt   — он же текстом, для секрета ANDROID_KEYSTORE_BASE64;
#   smsagent-signing.txt           — имена секретов GitHub, пароль, псевдоним и отпечаток ключа.
# Пароль создаётся случайный. Всю папку — Святу лично (флешкой и т. п.), не в Issue и не в чат с историей.
# Потерять ключ нельзя: без него новую версию не поставить поверх старой — только удалять приложение
# (вместе с его настройками) и ставить заново.

param(
    [string]$OutDir = (Join-Path $env:USERPROFILE "ClinicHub-keys"),
    [string]$Alias = "smsagent"
)

$ErrorActionPreference = "Stop"
$keystore = Join-Path $OutDir "smsagent.keystore"
$base64File = Join-Path $OutDir "smsagent.keystore.base64.txt"
$infoFile = Join-Path $OutDir "smsagent-signing.txt"

if (Test-Path $keystore) {
    throw "Ключ уже есть: $keystore. Второй не создаём — обновления подписываются только одним ключом."
}

# keytool входит в JDK; Visual Studio с MAUI ставит Microsoft OpenJDK, но обычно не добавляет его в PATH.
function Find-Keytool {
    $cmd = Get-Command keytool.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $roots = @($env:JAVA_HOME, "$env:ProgramFiles\Microsoft", "$env:ProgramFiles\Android\openjdk",
        "$env:ProgramFiles\Eclipse Adoptium", "$env:ProgramFiles\Java") | Where-Object { $_ -and (Test-Path $_) }
    foreach ($root in $roots) {
        $found = Get-ChildItem $root -Filter keytool.exe -Recurse -ErrorAction SilentlyContinue |
            Sort-Object FullName -Descending | Select-Object -First 1
        if ($found) { return $found.FullName }
    }
    return $null
}

$keytool = Find-Keytool
if (-not $keytool) {
    throw "Не найден keytool.exe (входит в JDK). Он ставится вместе с Visual Studio (нагрузка .NET MAUI)."
}
Write-Host "keytool: $keytool"

# Случайный пароль: буквы и цифры, без символов, которые ломаются в командной строке.
$chars = [char[]]"ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789"
$bytes = New-Object byte[] 32
[Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
$password = -join ($bytes | ForEach-Object { $chars[$_ % $chars.Length] })

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# Пароль передаём через переменную окружения, а не в командной строке (её видно в списке процессов).
$env:CLINICHUB_KEYSTORE_PASS = $password
try {
    # PKCS12: пароль ключа совпадает с паролем хранилища. 10000 дней — около 27 лет.
    & $keytool -genkeypair -keystore $keystore -storetype PKCS12 -alias $Alias -keyalg RSA -keysize 4096 `
        -validity 10000 -dname "CN=ClinicHub SMS, O=Regenika, C=RU" `
        -storepass:env CLINICHUB_KEYSTORE_PASS -keypass:env CLINICHUB_KEYSTORE_PASS
    if ($LASTEXITCODE -ne 0) { throw "keytool не смог создать ключ (код $LASTEXITCODE)." }

    $listing = & $keytool -list -v -keystore $keystore -alias $Alias -storepass:env CLINICHUB_KEYSTORE_PASS
    $fingerprint = ($listing | Select-String "SHA256:" | Select-Object -First 1).Line.Trim()
} finally {
    Remove-Item Env:\CLINICHUB_KEYSTORE_PASS -ErrorAction SilentlyContinue
}

[IO.File]::WriteAllText($base64File, [Convert]::ToBase64String([IO.File]::ReadAllBytes($keystore)))

$info = @"
Ключ подписи ClinicHub SMS (SmsAgent), создан $(Get-Date -Format "yyyy-MM-dd").
НЕ КЛАСТЬ В GIT. Передать Святу лично. Копию хранить в надёжном месте: без ключа обновить приложение поверх нельзя.

Секреты репозитория: GitHub → Regenika/ClinicHub → Settings → Secrets and variables → Actions → New repository secret

  ANDROID_KEYSTORE_BASE64   = всё содержимое файла smsagent.keystore.base64.txt (одной строкой)
  ANDROID_KEYSTORE_PASSWORD = $password
  ANDROID_KEY_ALIAS         = $Alias
  ANDROID_KEY_PASSWORD      = $password

Отпечаток сертификата (не секрет; по нему видно, что APK подписан именно этим ключом):
  $fingerprint
"@
[IO.File]::WriteAllText($infoFile, $info, (New-Object Text.UTF8Encoding($true)))

# Папку видит только текущий пользователь и администраторы.
& icacls.exe $OutDir /inheritance:r /grant:r "${env:USERNAME}:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" /C /Q | Out-Null

Write-Host ""
Write-Host "Готово. Ключ и файлы для Свята — в папке $OutDir" -ForegroundColor Green
Write-Host "  smsagent.keystore, smsagent.keystore.base64.txt, smsagent-signing.txt"
Write-Host "Пароль на экран не выводится — он в smsagent-signing.txt."
Write-Host "Отпечаток: $fingerprint"
