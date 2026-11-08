param(
    [switch]$Dump,
    [string]$Out,
    [switch]$Release
)

# Корень репозитория
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

# Путь к папке конкретного проекта внутри репозитория
$projectDir = Join-Path $root "TokenMonitor"

$configuration = if ($Release) { "Release" } else { "Debug" }

# Путь к исполняемому файлу внутри папки проекта
$exe = Join-Path $projectDir "bin\$configuration\net10.0-windows\TokenMonitor.exe"

if (-not (Test-Path -LiteralPath $exe)) {
    # Собираем проект, указывая правильный путь к .csproj
    dotnet build (Join-Path $projectDir "TokenMonitor.csproj") -c $configuration
    if (-not (Test-Path -LiteralPath $exe)) {
        throw "Сборка не создала $exe"
    }
}

if ($Dump) {
    if ([string]::IsNullOrWhiteSpace($Out)) {
        & $exe --dump
    } else {
        & $exe "--dump=$Out"
        Get-Content -LiteralPath $Out -Encoding UTF8
    }
    return
}

Start-Process -FilePath $exe
