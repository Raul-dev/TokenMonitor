<#
.SYNOPSIS
    Установка плагинов TokenMonitor: opencode, DeepSeek Harness или оба сразу.

.DESCRIPTION
    Без параметров скрипт показывает справку и ничего не меняет.
    Для установки укажите цель: -OpenCode, -DeepSeek или -All.

.PARAMETER OpenCode
    Установить плагины opencode: token-stats.ts (статистика токенов) и proxy.ts
    (локальный прокси) в каталог плагинов opencode.

.PARAMETER DeepSeek
    Установить host-плагин @local/dsh-token-logger в профиль desktop DeepSeek Harness.

.PARAMETER All
    Установить и плагины opencode, и плагин DeepSeek Harness.

.PARAMETER Check
    Только проверить установку, ничего не менять. Коды выхода: 0 — всё на месте,
    2 — установлено, но требует обновления, 3 — не установлено.

.PARAMETER Uninstall
    Удалить плагины выбранной цели. Требует -Force.

.PARAMETER Force
    Подтверждение удаления (для -Uninstall).

.EXAMPLE
    .\install.ps1 -OpenCode

.EXAMPLE
    .\install.ps1 -DeepSeek

.EXAMPLE
    .\install.ps1 -All

.EXAMPLE
    .\install.ps1 -Check -All

.EXAMPLE
    .\install.ps1 -Uninstall -OpenCode -Force
#>
[CmdletBinding()]
param(
    [switch]$OpenCode,
    [switch]$DeepSeek,
    [switch]$All,
    [switch]$Check,
    [switch]$Uninstall,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

function Write-Ok { param($text) Write-Host $text -ForegroundColor Green }
function Write-Warn { param($text) Write-Host $text -ForegroundColor Yellow }
function Write-Err { param($text) Write-Host $text -ForegroundColor Red }

function Show-Help {
    Write-Host ''
    Write-Host 'Token Monitor — установка плагинов' -ForegroundColor Cyan
    Write-Host ''
    Write-Host 'Назначение параметров:'
    Write-Host '  -OpenCode        плагины opencode: token-stats.ts (статистика токенов)'
    Write-Host '                   и proxy.ts (локальный прокси)'
    Write-Host '  -DeepSeek        host-плагин @local/dsh-token-logger для DeepSeek Harness'
    Write-Host '                   (профиль desktop, база ~/.dsh/tokens/tokens.db)'
    Write-Host '  -All             и opencode, и DeepSeek Harness'
    Write-Host '  -Check           только проверить установку, ничего не менять'
    Write-Host '  -Uninstall       удалить плагины выбранной цели'
    Write-Host '  -Force           подтверждение удаления (обязателен с -Uninstall)'
    Write-Host ''
    Write-Host 'Без параметров скрипт показывает эту справку и ничего не меняет.'
    Write-Host 'Если цель не указана, но указан -Check или -Uninstall, проверяются/'
    Write-Host 'удаляются обе цели (как -All).'
    Write-Host ''
    Write-Host 'Примеры:' -ForegroundColor Cyan
    Write-Host '  .\install.ps1 -OpenCode                  установить плагины opencode'
    Write-Host '  .\install.ps1 -DeepSeek                  установить плагин DeepSeek Harness'
    Write-Host '  .\install.ps1 -All                       установить всё'
    Write-Host '  .\install.ps1 -Check -All                проверить обе цели'
    Write-Host '  .\install.ps1 -Uninstall -OpenCode -Force'
    Write-Host '  .\install.ps1                            эта справка'
    Write-Host ''
    Write-Host 'Коды выхода: 0 — успех, 1 — ошибка, 2 — требуется обновление,'
    Write-Host '             3 — не установлено (для -Check).'
    Write-Host ''
}

# ---------------------------------------------------------------------------
#  opencode
# ---------------------------------------------------------------------------

$openCodeConfigRoot = if ($env:OPENCODE_CONFIG_DIR) { $env:OPENCODE_CONFIG_DIR } else { Join-Path $env:USERPROFILE '.config\opencode' }
$openCodePluginsDir = Join-Path $openCodeConfigRoot 'plugins'
$openCodeProxyConfig = Join-Path $openCodeConfigRoot 'token-monitor-proxy.json'
$openCodeDataDir = Join-Path $env:USERPROFILE '.local\share\opencode'

function Get-OpenCodePlugins {
    @(
        [pscustomobject]@{
            Name = 'token-stats'
            Title = 'статистика токенов'
            Source = Join-Path $PSScriptRoot 'OpenCodeSetup\token-stats.ts'
            Target = Join-Path $openCodePluginsDir 'token-stats.ts'
            SourceHash = $null
            Installed = $false
            TargetHash = $null
            UpToDate = $false
        },
        [pscustomobject]@{
            Name = 'proxy'
            Title = 'локальный прокси'
            Source = Join-Path $PSScriptRoot 'OpenCodeSetup\proxy.ts'
            Target = Join-Path $openCodePluginsDir 'proxy.ts'
            SourceHash = $null
            Installed = $false
            TargetHash = $null
            UpToDate = $false
        }
    )
}

function Read-OpenCodePluginState {
    $plugins = Get-OpenCodePlugins
    foreach ($plugin in $plugins) {
        $plugin.Installed = Test-Path -LiteralPath $plugin.Target
        $plugin.TargetHash = if ($plugin.Installed) { (Get-FileHash -LiteralPath $plugin.Target -Algorithm SHA256).Hash } else { $null }
        if (Test-Path -LiteralPath $plugin.Source) {
            $plugin.SourceHash = (Get-FileHash -LiteralPath $plugin.Source -Algorithm SHA256).Hash
            $plugin.UpToDate = $plugin.Installed -and ($plugin.TargetHash -eq $plugin.SourceHash)
        }
    }
    $plugins
}

function Show-OpenCodeTargets {
    Write-Host ''
    Write-Host 'Token Monitor — плагины opencode' -ForegroundColor Cyan
    foreach ($plugin in (Get-OpenCodePlugins)) {
        Write-Host "  $($plugin.Name) ($($plugin.Title))"
        Write-Host "    источник: $($plugin.Source)"
        Write-Host "    цель:     $($plugin.Target)"
    }
    Write-Host "  конфиг прокси: $openCodeProxyConfig"
    Write-Host ''
}

function Show-OpenCodeDataFiles {
    Write-Host 'Файлы, которые создают плагины:'
    Write-Host "  $openCodeDataDir\token-stats.log"
    Write-Host "  $openCodeDataDir\token-events.jsonl"
    Write-Host "  $openCodeDataDir\token-proxy.log"
}

function Show-OpenCodeNextSteps {
    Write-Host ''
    Write-Host 'Что дальше:' -ForegroundColor Cyan
    Write-Host '  1. Перезапустите opencode — плагины загружаются только при старте.'
    Write-Host '  2. Убедитесь, что TokenMonitor запущен и локальный прокси слушает адрес из proxyUrl.'
    Write-Host '  3. Выполните любой запрос: появятся файлы логов (см. ниже).'
    Write-Host '  4. Запустите приложение: .\run.ps1'
    Write-Host ''
    Write-Host 'Настройка прокси-плагина:'
    Write-Host "  файл: $openCodeProxyConfig"
    Write-Host '  поля: enabled (true/false), proxyUrl ("http://IP:PORT"), disableDirectFallback (true/false)'
    Write-Host '  TOKEN_MONITOR_PROXY_URL        — переопределить proxyUrl'
    Write-Host '  TOKEN_MONITOR_PROXY_DISABLED=1 — принудительно отключить'
    Show-OpenCodeDataFiles
}

function Ensure-OpenCodeProxyConfig {
    if (Test-Path -LiteralPath $openCodeProxyConfig) {
        return
    }

    $defaultProxyConfig = @{
        enabled = $true
        proxyUrl = 'http://127.0.0.1:8181'
        disableDirectFallback = $false
    } | ConvertTo-Json
    [IO.File]::WriteAllText($openCodeProxyConfig, $defaultProxyConfig + [Environment]::NewLine, (New-Object System.Text.UTF8Encoding($false)))
    Write-Ok "Создан конфиг прокси-плагина: $openCodeProxyConfig"
}

function Invoke-OpenCodeCheck {
    $plugins = Read-OpenCodePluginState
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
        Show-OpenCodeDataFiles
        return 'missing'
    }
    if ($outdated.Count -gt 0) { return 'outdated' }
    return 'ok'
}

function Invoke-OpenCodeUninstall {
    $plugins = Read-OpenCodePluginState
    $installed = @($plugins | Where-Object { $_.Installed })
    if ($installed.Count -eq 0) {
        Write-Warn 'Плагины opencode не установлены — удалять нечего.'
        return 'ok'
    }

    foreach ($plugin in $installed) {
        Remove-Item -LiteralPath $plugin.Target -Force
        Write-Ok "Удалён: $($plugin.Target)"
    }

    Write-Warn 'Не забудьте перезапустить opencode, чтобы плагины перестали загружаться.'
    return 'ok'
}

function Invoke-OpenCodeInstall {
    Show-OpenCodeTargets

    $plugins = Read-OpenCodePluginState
    $missingSources = @($plugins | Where-Object { -not (Test-Path -LiteralPath $_.Source) })
    if ($missingSources.Count -gt 0) {
        foreach ($plugin in $missingSources) {
            Write-Err "Не найден файл плагина: $($plugin.Source)"
        }
        return 'error'
    }

    if (Test-Path -LiteralPath $openCodeConfigRoot) {
        Ensure-OpenCodeProxyConfig
    }

    $pending = @($plugins | Where-Object { -not $_.UpToDate })
    if ($pending.Count -eq 0) {
        Write-Ok 'Оба плагина opencode уже установлены, версии совпадают — изменений не требуется.'
        Show-OpenCodeDataFiles
        return 'ok'
    }

    if (-not (Test-Path -LiteralPath $openCodeConfigRoot)) {
        Write-Err "Не найден каталог конфигурации opencode: $openCodeConfigRoot"
        Write-Err 'Запустите opencode хотя бы один раз и повторите установку.'
        return 'error'
    }

    $dbPath = Join-Path $openCodeDataDir 'opencode.db'
    if (-not (Test-Path -LiteralPath $dbPath)) {
        Write-Err "Не найдена база данных opencode: $dbPath"
        Write-Err 'Запустите opencode и выполните хотя бы один запрос, затем повторите установку.'
        return 'error'
    }

    New-Item -ItemType Directory -Path $openCodePluginsDir -Force | Out-Null
    Ensure-OpenCodeProxyConfig
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
            return 'error'
        }

        Write-Ok "Установлено: $($plugin.Target)"
    }

    Show-OpenCodeNextSteps
    return 'ok'
}

# ---------------------------------------------------------------------------
#  DeepSeek Harness
# ---------------------------------------------------------------------------

$dshHome = if ($env:DSH_HOME) { $env:DSH_HOME } else { Join-Path $env:USERPROFILE '.dsh' }
$dshProfileName = 'desktop'
$dshProfileDir = Join-Path $dshHome "profiles\$dshProfileName"
$dshProfileJson = Join-Path $dshProfileDir 'package.json'
$dshPluginSource = Join-Path $PSScriptRoot 'DeepSeekSetup\plugin'
$dshPluginPackageJson = Join-Path $dshPluginSource 'package.json'
$dshPluginVersion = if (Test-Path -LiteralPath $dshPluginPackageJson) { (Get-Content -LiteralPath $dshPluginPackageJson -Raw | ConvertFrom-Json).version } else { $null }
$dshBundleName = '@local/dsh-token-logger'
$dshDbPath = Join-Path $dshHome 'tokens\tokens.db'

function Find-DshCli {
    $candidates = @()
    if ($env:DSH_CLI) { $candidates += $env:DSH_CLI }
    if ($env:LOCALAPPDATA) { $candidates += (Join-Path $env:LOCALAPPDATA 'Programs\DeepSeek Harness\resources\runtime\cli\bin\dsh.cmd') }
    if ($env:ProgramFiles) { $candidates += (Join-Path $env:ProgramFiles 'DeepSeek Harness\resources\runtime\cli\bin\dsh.cmd') }
    if (${env:ProgramFiles(x86)}) { $candidates += (Join-Path ${env:ProgramFiles(x86)} 'DeepSeek Harness\resources\runtime\cli\bin\dsh.cmd') }

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }

    $command = Get-Command dsh -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    return $null
}

function Get-JsonProperty {
    param($Object, [string]$Name)
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($property) { return $property.Value }
    return $null
}

function Get-DshManifest {
    if (-not (Test-Path -LiteralPath $dshProfileJson)) { return $null }
    return Get-Content -LiteralPath $dshProfileJson -Raw | ConvertFrom-Json
}

function Get-DshState {
    $manifest = Get-DshManifest
    $dependencies = Get-JsonProperty $manifest 'dependencies'
    $dependency = Get-JsonProperty $dependencies $dshBundleName
    $profile = Get-JsonProperty (Get-JsonProperty $manifest 'dsh') 'profile'
    $bundles = @(Get-JsonProperty $profile 'bundles' | Where-Object { $_ })

    $linkedPath = $null
    if ($dependency -is [string]) {
        $linkedPath = $dependency
        foreach ($prefix in @('link:', 'file:')) {
            if ($linkedPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
                $linkedPath = $linkedPath.Substring($prefix.Length)
                break
            }
        }
        $linkedPath = $linkedPath -replace '/', '\'
    }

    [pscustomobject]@{
        HasProfile = (Test-Path -LiteralPath $dshProfileJson)
        Dependency = $dependency
        LinkedPath = $linkedPath
        InBundles = ($bundles -contains $dshBundleName)
        Bundles = $bundles
        SourceExists = (Test-Path -LiteralPath $dshPluginPackageJson)
        DbPath = $dshDbPath
        DbExists = (Test-Path -LiteralPath $dshDbPath)
    }
}

function Write-DshManifest {
    param($Manifest)

    if (Test-Path -LiteralPath $dshProfileJson) {
        $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
        $backup = $dshProfileJson + '.bak-' + $stamp
        Copy-Item -LiteralPath $dshProfileJson -Destination $backup -Force
        Write-Warn "Копия манифеста профиля: $backup"
    }

    $json = $Manifest | ConvertTo-Json -Depth 12
    [IO.File]::WriteAllText($dshProfileJson, $json + [Environment]::NewLine, (New-Object System.Text.UTF8Encoding($false)))
}

function Set-DshBundleRegistered {
    $manifest = Get-DshManifest
    $profile = Get-JsonProperty (Get-JsonProperty $manifest 'dsh') 'profile'
    $bundles = @(Get-JsonProperty $profile 'bundles' | Where-Object { $_ })

    if ($bundles -contains $dshBundleName) { return $false }
    $bundles += $dshBundleName

    if (-not $manifest.PSObject.Properties['dsh']) {
        $manifest | Add-Member -NotePropertyName dsh -NotePropertyValue ([pscustomobject]@{ profile = [pscustomobject]@{ bundles = $bundles } })
    } elseif (-not $manifest.dsh.PSObject.Properties['profile']) {
        $manifest.dsh | Add-Member -NotePropertyName profile -NotePropertyValue ([pscustomobject]@{ bundles = $bundles })
    } else {
        $manifest.dsh.profile | Add-Member -NotePropertyName bundles -NotePropertyValue $bundles -Force
    }

    Write-DshManifest $manifest
    return $true
}

function Remove-DshBundleRegistration {
    $manifest = Get-DshManifest
    $profile = Get-JsonProperty (Get-JsonProperty $manifest 'dsh') 'profile'
    $bundles = @(Get-JsonProperty $profile 'bundles' | Where-Object { $_ })
    if ($bundles -notcontains $dshBundleName) { return $false }

    $remaining = @($bundles | Where-Object { $_ -ne $dshBundleName })
    $manifest.dsh.profile | Add-Member -NotePropertyName bundles -NotePropertyValue $remaining -Force
    Write-DshManifest $manifest
    return $true
}

function Show-DeepSeekTargets {
    Write-Host ''
    Write-Host 'Token Monitor — плагин DeepSeek Harness' -ForegroundColor Cyan
    Write-Host "  плагин:  $dshBundleName $dshPluginVersion"
    Write-Host "  источник: $dshPluginSource"
    Write-Host "  профиль:  $dshProfileDir"
    Write-Host "  база:     $dshDbPath"
    Write-Host ''
}

function Invoke-DeepSeekCheck {
    $state = Get-DshState
    $problems = @()

    if (-not $state.HasProfile) {
        Write-Warn "Профиль DeepSeek Harness не найден: $dshProfileJson"
        Write-Warn 'Запустите DeepSeek Harness Desktop хотя бы один раз.'
        return 'missing'
    }
    if (-not $state.SourceExists) {
        Write-Err "Не найден плагин: $dshPluginPackageJson"
        return 'error'
    }

    Write-Host "  зависимость профиля: $(if ($state.Dependency) { $state.Dependency } else { 'нет' })"
    Write-Host "  ссылка на исходники: $(if ($state.LinkedPath) { $state.LinkedPath } else { '—' })"
    Write-Host "  в dsh.profile.bundles: $(if ($state.InBundles) { 'да' } else { 'нет' })"
    Write-Host "  база токенов: $dshDbPath ($(if ($state.DbExists) { 'есть' } else { 'ещё не создана' }))"

    if (-not $state.Dependency) { $problems += 'плагин не добавлен в зависимости профиля' }
    if ($state.LinkedPath) {
        $expected = [IO.Path]::GetFullPath($dshPluginSource)
        $actual = [IO.Path]::GetFullPath($state.LinkedPath)
        if ($actual -ne $expected) { $problems += "ссылка указывает на другой каталог: $($state.LinkedPath)" }
    }
    if (-not $state.InBundles) { $problems += 'плагин не включён в dsh.profile.bundles' }

    if ($problems.Count -gt 0) {
        foreach ($problem in $problems) { Write-Warn "  проблема: $problem" }
        return $(if ($state.Dependency -or $state.InBundles) { 'outdated' } else { 'missing' })
    }

    Write-Ok "DeepSeek: плагин установлен и включён ($dshBundleName $dshPluginVersion)."
    if (-not $state.DbExists) {
        Write-Warn 'База токенов ещё не создана: выполните запрос в DeepSeek Harness.'
    }
    return 'ok'
}

function Invoke-DeepSeekInstall {
    Show-DeepSeekTargets

    if (-not (Test-Path -LiteralPath $dshPluginPackageJson)) {
        Write-Err "Не найден плагин: $dshPluginPackageJson"
        return 'error'
    }
    if (-not (Test-Path -LiteralPath $dshProfileJson)) {
        Write-Err "Не найден профиль DeepSeek Harness: $dshProfileJson"
        Write-Err 'Запустите DeepSeek Harness Desktop хотя бы один раз и повторите установку.'
        return 'error'
    }

    $state = Get-DshState
    if ($state.Dependency -and $state.InBundles -and $state.LinkedPath) {
        $expected = [IO.Path]::GetFullPath($dshPluginSource)
        $actual = [IO.Path]::GetFullPath($state.LinkedPath)
        if ($actual -eq $expected) {
            Write-Ok "DeepSeek: плагин уже установлен и включён ($dshBundleName $dshPluginVersion)."
            Write-Host 'Перезапустите DeepSeek Harness, если он был запущен до установки.'
            return 'ok'
        }
    }

    $dshCli = Find-DshCli
    if (-not $dshCli) {
        Write-Err 'Не найден CLI DeepSeek Harness (dsh).'
        Write-Err 'Установите плагин вручную: DeepSeek Harness → Plugins → Add plugin →'
        Write-Err "  $dshPluginSource"
        return 'error'
    }

    Write-Host "Установка зависимости профиля через: $dshCli" -ForegroundColor Cyan
    & $dshCli plugin --profile $dshProfileName add $dshPluginSource
    if ($LASTEXITCODE -ne 0) {
        Write-Err "Не удалось добавить плагин в профиль (pnpm exit=$LASTEXITCODE)."
        Write-Err 'Можно установить вручную: DeepSeek Harness → Plugins → Add plugin →'
        Write-Err "  $dshPluginSource"
        return 'error'
    }

    if (Set-DshBundleRegistered) {
        Write-Ok "Плагин включён в dsh.profile.bundles ($dshBundleName)."
    } else {
        Write-Ok "Плагин уже был включён в dsh.profile.bundles ($dshBundleName)."
    }

    Write-Host ''
    Write-Host 'Что дальше:' -ForegroundColor Cyan
    Write-Host '  1. Перезапустите DeepSeek Harness Desktop — плагин загружается при старте.'
    Write-Host '  2. Выполните любой запрос в агенте: появится база токенов.'
    Write-Host "     $dshDbPath"
    Write-Host '  3. В TokenMonitor выберите источник DeepSeek в списке сверху.'
    Write-Host '  4. Настройки сборщика — строка token-logger в файле:'
    Write-Host "     $(Join-Path $dshProfileDir 'cordis.patch.yml')"
    return 'ok'
}

function Invoke-DeepSeekUninstall {
    $state = Get-DshState
    if (-not $state.HasProfile) {
        Write-Warn "Профиль DeepSeek Harness не найден: $dshProfileJson"
        return 'ok'
    }

    if (-not $state.Dependency -and -not $state.InBundles) {
        Write-Warn 'Плагин DeepSeek не установлен — удалять нечего.'
        return 'ok'
    }

    $dshCli = Find-DshCli
    $removed = $false
    if ($dshCli -and $state.Dependency) {
        Write-Host "Удаление зависимости профиля через: $dshCli" -ForegroundColor Cyan
        & $dshCli plugin --profile $dshProfileName remove $dshBundleName
        if ($LASTEXITCODE -eq 0) {
            $removed = $true
        } else {
            Write-Warn "pnpm не смог удалить пакет (exit=$LASTEXITCODE); продолжаю отключать плагин."
        }
    } elseif (-not $dshCli) {
        Write-Warn 'CLI DeepSeek Harness не найден — удаляю только запись из dsh.profile.bundles.'
    }

    if (Remove-DshBundleRegistration) {
        $removed = $true
    }

    if ($removed) {
        Write-Ok "Плагин удалён из профиля ($dshBundleName)."
    }

    Write-Warn 'Перезапустите DeepSeek Harness, чтобы плагин перестал загружаться.'
    if (Test-Path -LiteralPath $dshDbPath) {
        Write-Warn "База токенов сохранена (удалите вручную при необходимости): $dshDbPath"
    }
    return 'ok'
}

# ---------------------------------------------------------------------------
#  main
# ---------------------------------------------------------------------------

if ($PSBoundParameters.Count -eq 0) {
    Show-Help
    exit 0
}

if ($Check -and $Uninstall) {
    Write-Err 'Нельзя одновременно указывать -Check и -Uninstall.'
    exit 1
}

if ($Uninstall -and -not $Force) {
    Write-Err 'Удаление требует явного подтверждения: повторите с параметром -Force.'
    exit 1
}

$hasAction = [bool]$Check -or [bool]$Uninstall
$hasTarget = [bool]$OpenCode -or [bool]$DeepSeek -or [bool]$All
if (-not $hasAction -and -not $hasTarget) {
    # Указан только -Force: показываем справку, как и без параметров.
    Show-Help
    exit 0
}

$doOpenCode = [bool]$OpenCode -or [bool]$All
$doDeepSeek = [bool]$DeepSeek -or [bool]$All
if (-not $doOpenCode -and -not $doDeepSeek) {
    # Указан только -Check/-Uninstall/-Force — проверяем/удаляем обе цели.
    $doOpenCode = $true
    $doDeepSeek = $true
}

$mode = if ($Check) { 'check' } elseif ($Uninstall) { 'uninstall' } else { 'install' }
$statuses = @()

Write-Host ''
Write-Host "Token Monitor — режим: $mode" -ForegroundColor Cyan

if ($doOpenCode) {
    $statuses += switch ($mode) {
        'check' { Invoke-OpenCodeCheck }
        'uninstall' { Invoke-OpenCodeUninstall }
        default { Invoke-OpenCodeInstall }
    }
}

if ($doDeepSeek) {
    $statuses += switch ($mode) {
        'check' { Invoke-DeepSeekCheck }
        'uninstall' { Invoke-DeepSeekUninstall }
        default { Invoke-DeepSeekInstall }
    }
}

if ($statuses -contains 'error') { exit 1 }
if ($statuses -contains 'missing') { exit 3 }
if ($statuses -contains 'outdated') { exit 2 }
exit 0
