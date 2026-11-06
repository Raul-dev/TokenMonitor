[CmdletBinding()]
param(
    [switch]$Check,
    [switch]$Uninstall,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

function Write-Ok { param($text) Write-Host $text -ForegroundColor Green }
function Write-Warn { param($text) Write-Host $text -ForegroundColor Yellow }
function Write-Err { param($text) Write-Host $text -ForegroundColor Red }

$source = Join-Path $PSScriptRoot 'TokenMonitor\OpenCodeSetup\token-stats.ts'
$configRoot = if ($env:OPENCODE_CONFIG_DIR) { $env:OPENCODE_CONFIG_DIR } else { Join-Path $env:USERPROFILE '.config\opencode' }
$pluginsDir = Join-Path $configRoot 'plugins'
$target = Join-Path $pluginsDir 'token-stats.ts'
$dataDir = Join-Path $env:USERPROFILE '.local\share\opencode'

Write-Host ''
Write-Host 'Token Monitor — плагин статистики токенов' -ForegroundColor Cyan
Write-Host "  источник: $source"
Write-Host "  цель:     $target"
Write-Host ''

if ($Check -and $Uninstall) {
    Write-Err 'Нельзя одновременно указывать -Check и -Uninstall.'
    exit 1
}

if (-not (Test-Path -LiteralPath $source)) {
    Write-Err "Не найден файл плагина: $source"
    exit 1
}

$sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
$installed = Test-Path -LiteralPath $target
$targetHash = if ($installed) { (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash } else { $null }
$upToDate = $installed -and ($targetHash -eq $sourceHash)

function Show-DataFiles {
    Write-Host 'Файлы, которые создаёт плагин:'
    Write-Host "  $dataDir\token-stats.log"
    Write-Host "  $dataDir\token-events.jsonl"
}

function Show-NextSteps {
    Write-Host ''
    Write-Host 'Что дальше:' -ForegroundColor Cyan
    Write-Host '  1. Перезапустите opencode — плагины загружаются только при старте.'
    Write-Host '  2. Выполните любой запрос: появятся файлы логов (см. ниже).'
    Write-Host '  3. Запустите приложение: .\run.ps1'
    Show-DataFiles
}

if ($Check) {
    if (-not $installed) {
        Write-Warn 'Плагин не установлен.'
        Show-DataFiles
        exit 3
    }

    if ($upToDate) {
        Write-Ok 'Плагин установлен, версия совпадает с проектом.'
        exit 0
    }

    Write-Warn 'Плагин установлен, но отличается от версии в проекте (нужно обновить).'
    Write-Host "  в проекте: $sourceHash"
    Write-Host "  на диске:  $targetHash"
    exit 2
}

if ($Uninstall) {
    if (-not $installed) {
        Write-Warn 'Плагин не установлен — удалять нечего.'
        exit 0
    }

    if (-not $Force) {
        Write-Err 'Удаление требует явного подтверждения: повторите с параметром -Force.'
        exit 1
    }

    Remove-Item -LiteralPath $target -Force
    Write-Ok "Удалён: $target"
    Write-Warn 'Не забудьте перезапустить opencode, чтобы плагин перестал загружаться.'
    exit 0
}

if ($upToDate) {
    Write-Ok 'Плагин уже установлен, версия совпадает — изменений не требуется.'
    Show-DataFiles
    exit 0
}

if (-not (Test-Path -LiteralPath $configRoot)) {
    Write-Err "Не найден каталог конфигурации opencode: $configRoot"
    Write-Err 'Запустите opencode хотя бы один раз и повторите установку.'
    exit 1
}

$dbPath = Join-Path $dataDir 'opencode.db'
if (-not (Test-Path -LiteralPath $dbPath)) {
    Write-Err "Не найдена база данных opencode: $dbPath"
    Write-Err 'Запустите opencode и выполните хотя бы один запрос, затем повторите установку.'
    exit 1
}

New-Item -ItemType Directory -Path $pluginsDir -Force | Out-Null

if ($installed) {
    $backup = $target + '.bak-' + (Get-Date -Format 'yyyyMMdd-HHmmss')
    Copy-Item -LiteralPath $target -Destination $backup -Force
    Write-Warn "Прежняя версия сохранена: $backup"
}

Copy-Item -LiteralPath $source -Destination $target -Force

$newHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
if ($newHash -ne $sourceHash) {
    Write-Err 'Установка не удалась: хэш скопированного файла не совпадает с исходным.'
    exit 1
}

Write-Ok "Установлено: $target"
Show-NextSteps
exit 0
