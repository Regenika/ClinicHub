# Сборка архива-установщика сайта для компьютеров клиники. Запускать на машине разработчика (Windows, .NET 10 SDK):
#   powershell -ExecutionPolicy Bypass -File deploy\web\build-package.ps1
# Результат: artifacts\ClinicHub-Web-<дата>.zip — его и копируем на компьютер клиники.
#
# По умолчанию сборка автономная (self-contained, win-x64): .NET ставить на компьютер клиники не нужно.
# Ключ -Portable собирает маленький архив, которому нужен установленный ASP.NET Core Runtime 10.

param(
    [switch]$Portable,
    # Только папка artifacts\web-package, без zip (так собирает автообновление: архив ему не нужен).
    [switch]$NoZip
)

$ErrorActionPreference = "Stop"
$repo = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$stamp = Get-Date -Format "yyyy-MM-dd"
$staging = Join-Path $repo "artifacts\web-package"
$zip = Join-Path $repo "artifacts\ClinicHub-Web-$stamp.zip"

Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $staging | Out-Null

$project = Join-Path $repo "ClinicHub.Web\ClinicHub.Web.csproj"
$out = Join-Path $staging "app"
if ($Portable) {
    dotnet publish $project -c Release -o $out
} else {
    dotnet publish $project -c Release -r win-x64 --self-contained -o $out
}
if ($LASTEXITCODE -ne 0) { throw "dotnet publish завершился с ошибкой" }

# Настройки для разработки на компьютере клиники не нужны.
Remove-Item (Join-Path $out "appsettings.Development.json") -ErrorAction SilentlyContinue

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
