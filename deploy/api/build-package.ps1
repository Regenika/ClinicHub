# Сборка архива-установщика сервера для компьютера клиники (#48). Запускать на машине разработчика (Windows, .NET 10 SDK):
#   powershell -ExecutionPolicy Bypass -File deploy\api\build-package.ps1
# Результат: artifacts\ClinicHub-Api-<дата>.zip — его копируем на компьютер клиники.
# Сборка автономная (self-contained, win-x64): .NET ставить на компьютер клиники не нужно.

param(
    # Только папка artifacts\api-package, без zip (так собирает автообновление).
    [switch]$NoZip
)

$ErrorActionPreference = "Stop"
$repo = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$stamp = Get-Date -Format "yyyy-MM-dd"
$staging = Join-Path $repo "artifacts\api-package"
$zip = Join-Path $repo "artifacts\ClinicHub-Api-$stamp.zip"

Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $staging | Out-Null

$project = Join-Path $repo "ClinicHub.Api\ClinicHub.Api.csproj"
$out = Join-Path $staging "app"
dotnet publish $project -c Release -r win-x64 --self-contained -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish завершился с ошибкой" }

# Настройки и секреты разработки на компьютер клиники не попадают.
Remove-Item (Join-Path $out "appsettings.Development.json") -ErrorAction SilentlyContinue
Remove-Item (Join-Path $out "http-client.env.json*") -ErrorAction SilentlyContinue
Remove-Item (Join-Path $out "logs") -Recurse -Force -ErrorAction SilentlyContinue

foreach ($f in "install.cmd", "install.ps1", "uninstall.cmd", "uninstall.ps1", "README.txt") {
    Copy-Item (Join-Path $PSScriptRoot $f) $staging
}

if ($NoZip) {
    Write-Host "Готово: $staging" -ForegroundColor Green
    return
}
Remove-Item $zip -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $staging "*") -DestinationPath $zip
Write-Host "Готово: $zip" -ForegroundColor Green
