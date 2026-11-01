param(
    [switch]$Dump,
    [string]$Out,
    [switch]$Release
)

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$configuration = if ($Release) { "Release" } else { "Debug" }
$exe = Join-Path $root "bin\$configuration\net10.0-windows\TokenMonitor.exe"

if (-not (Test-Path -LiteralPath $exe)) {
    dotnet build (Join-Path $root "TokenMonitor.csproj") -c $configuration
    if (-not (Test-Path -LiteralPath $exe)) {
        throw "Сборка не создала $exe"
    }
}

if ($Dump) {
    if ([string]::IsNullOrWhiteSpace($Out)) {
        & $exe --dump
    }
    else {
        & $exe "--dump=$Out"
        Get-Content -LiteralPath $Out -Encoding UTF8
    }
    return
}

Start-Process -FilePath $exe
