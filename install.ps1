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

$configRoot = if ($env:OPENCODE_CONFIG_DIR) { $env:OPENCODE_CONFIG_DIR } else { Join-Path $env:USERPROFILE '.config\opencode' }
$pluginsDir = Join-Path $configRoot 'plugins'
$proxyConfig = Join-Path $configRoot 'token-monitor-proxy.json'
$dataDir = Join-Path $env:USERPROFILE '.local\share\opencode'

$plugins = @(
    [pscustomobject]@{
        Name = 'token-stats'
        Title = 'статистика токенов'
        Source = Join-Path $PSScriptRoot 'TokenMonitor\OpenCodeSetup\token-stats.ts'
        Target = Join-Path $pluginsDir 'token-stats.ts'
        SourceHash = $null
        Installed = $false
        TargetHash = $null
        UpToDate = $false
    },
    [pscustomobject]@{
        Name = 'proxy'
        Title = 'локальный прокси'
        Source = Join-Path $PSScriptRoot 'TokenMonitor\OpenCodeSetup\proxy.ts'
        Target = Join-Path $pluginsDir 'proxy.ts'
        SourceHash = $null
        Installed = $false
        TargetHash = $null
        UpToDate = $false
    }
)

Write-Host ''
Write-Host 'Token Monitor — плагины opencode' -ForegroundColor Cyan
foreach ($plugin in $plugins) {
    Write-Host "  $($plugin.Name) ($($plugin.Title))"
    Write-Host "    источник: $($plugin.Source)"
    Write-Host "    цель:     $($plugin.Target)"
}
Write-Host "  конфиг прокси: $proxyConfig"
Write-Host ''

if ($Check -and $Uninstall) {
    Write-Err 'Нельзя одновременно указывать -Check и -Uninstall.'
    exit 1
}

$missingSources = @($plugins | Where-Object { -not (Test-Path -LiteralPath $_.Source) })
if ($missingSources.Count -gt 0) {
    foreach ($plugin in $missingSources) {
        Write-Err "Не найден файл плагина: $($plugin.Source)"
    }
    exit 1
}

foreach ($plugin in $plugins) {
    $plugin.SourceHash = (Get-FileHash -LiteralPath $plugin.Source -Algorithm SHA256).Hash
    $plugin.Installed = Test-Path -LiteralPath $plugin.Target
    $plugin.TargetHash = if ($plugin.Installed) { (Get-FileHash -LiteralPath $plugin.Target -Algorithm SHA256).Hash } else { $null }
    $plugin.UpToDate = $plugin.Installed -and ($plugin.TargetHash -eq $plugin.SourceHash)
}

function Show-DataFiles {
    Write-Host 'Файлы, которые создают плагины:'
    Write-Host "  $dataDir\token-stats.log"
    Write-Host "  $dataDir\token-events.jsonl"
    Write-Host "  $dataDir\token-proxy.log"
}

function Show-NextSteps {
    Write-Host ''
    Write-Host 'Что дальше:' -ForegroundColor Cyan
    Write-Host '  1. Перезапустите opencode — плагины загружаются только при старте.'
    Write-Host '  2. Убедитесь, что TokenMonitor запущен и локальный прокси слушает адрес из proxyUrl.'
    Write-Host '  3. Выполните любой запрос: появятся файлы логов (см. ниже).'
    Write-Host '  4. Запустите приложение: .\run.ps1'
    Write-Host ''
    Write-Host 'Настройка прокси-плагина:'
    Write-Host "  файл: $proxyConfig"
    Write-Host '  поля: enabled (true/false), proxyUrl ("http://IP:PORT"), disableDirectFallback (true/false)'
    Write-Host '  TOKEN_MONITOR_PROXY_URL        — переопределить proxyUrl'
    Write-Host '  TOKEN_MONITOR_PROXY_DISABLED=1 — принудительно отключить'
    Show-DataFiles
}

function Ensure-ProxyConfig {
    if (Test-Path -LiteralPath $proxyConfig) {
        return
    }

    $defaultProxyConfig = @{
        enabled = $true
        proxyUrl = 'http://127.0.0.1:8181'
        disableDirectFallback = $false
    } | ConvertTo-Json
    [IO.File]::WriteAllText($proxyConfig, $defaultProxyConfig + [Environment]::NewLine, (New-Object System.Text.UTF8Encoding($false)))
    Write-Ok "Создан конфиг прокси-плагина: $proxyConfig"
}

if ($Check) {
    $missing = @($plugins | Where-Object { -not $_.Installed })
    $outdated = @($plugins | Where-Object { $_.Installed -and -not $_.UpToDate })

    foreach ($plugin in $plugins) {
        if ($plugin.UpToDate) {
            Write-Ok "$($plugin.Name): установлен, версия совпадает с проектом."
        } elseif ($plugin.Installed) {
            Write-Warn "$($plugin.Name): установлен, но отличается от версии в проекте (нужно обновить)."
            Write-Host "  в проекте: $($plugin.SourceHash)"
            Write-Host "  на диске:  $($plugin.TargetHash)"
        } else {
            Write-Warn "$($plugin.Name): не установлен."
        }
    }

    if ($missing.Count -gt 0) {
        Show-DataFiles
        exit 3
    }

    if ($outdated.Count -gt 0) {
        exit 2
    }

    exit 0
}

if ($Uninstall) {
    $installedPlugins = @($plugins | Where-Object { $_.Installed })
    if ($installedPlugins.Count -eq 0) {
        Write-Warn 'Плагины не установлены — удалять нечего.'
        exit 0
    }

    if (-not $Force) {
        Write-Err 'Удаление требует явного подтверждения: повторите с параметром -Force.'
        exit 1
    }

    foreach ($plugin in $installedPlugins) {
        Remove-Item -LiteralPath $plugin.Target -Force
        Write-Ok "Удалён: $($plugin.Target)"
    }

    Write-Warn 'Не забудьте перезапустить opencode, чтобы плагины перестали загружаться.'
    exit 0
}

if (Test-Path -LiteralPath $configRoot) {
    Ensure-ProxyConfig
}

$pending = @($plugins | Where-Object { -not $_.UpToDate })
if ($pending.Count -eq 0) {
    Write-Ok 'Оба плагина уже установлены, версии совпадают — изменений не требуется.'
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
Ensure-ProxyConfig
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'

foreach ($plugin in $pending) {
    if ($plugin.Installed) {
        $backup = $plugin.Target + '.bak-' + $stamp
        Copy-Item -LiteralPath $plugin.Target -Destination $backup -Force
        Write-Warn "Прежняя версия $($plugin.Name) сохранена: $backup"
    }

    Copy-Item -LiteralPath $plugin.Source -Destination $plugin.Target -Force

    $newHash = (Get-FileHash -LiteralPath $plugin.Target -Algorithm SHA256).Hash
    if ($newHash -ne $plugin.SourceHash) {
        Write-Err "Установка $($plugin.Name) не удалась: хэш скопированного файла не совпадает с исходным."
        exit 1
    }

    Write-Ok "Установлено: $($plugin.Target)"
}

Show-NextSteps
exit 0
