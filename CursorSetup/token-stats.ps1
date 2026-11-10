# Token Monitor — хук Cursor: пишет расход токена в jsonl.
# Ставится в %USERPROFILE%\.cursor\hooks.json, вызывается на beforeSubmitPrompt и stop.
$ErrorActionPreference = 'Stop'
try {
    [Console]::InputEncoding = New-Object System.Text.UTF8Encoding $false
    [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false
} catch {}

function Get-Text($obj, [string]$name) {
    if ($null -eq $obj) { return '' }
    $value = $obj.$name
    if ($null -eq $value) { return '' }
    return [string]$value
}

function Get-Long($obj, [string]$name) {
    if ($null -eq $obj) { return [int64]0 }
    $value = $obj.$name
    if ($null -eq $value -or $value -eq '') { return [int64]0 }
    try { return [int64]$value } catch { return [int64]0 }
}

$raw = [Console]::In.ReadToEnd()
if ([string]::IsNullOrWhiteSpace($raw)) { exit 0 }

$appDir = Join-Path $env:APPDATA 'TokenMonitor'
$pendingDir = Join-Path $appDir 'cursor-pending'
$eventsPath = Join-Path $appDir 'cursor-events.jsonl'
$logPath = Join-Path $appDir 'cursor-token-stats.log'

New-Item -ItemType Directory -Path $pendingDir -Force | Out-Null

try {
    $payload = $raw | ConvertFrom-Json
} catch {
    Add-Content -LiteralPath $logPath -Value ("{0:o} parse error: {1}" -f [DateTimeOffset]::UtcNow, $_.Exception.Message)
    exit 0
}

$eventName = Get-Text $payload 'hook_event_name'
$generationId = Get-Text $payload 'generation_id'
$conversationId = Get-Text $payload 'conversation_id'
if (-not $conversationId) { $conversationId = Get-Text $payload 'session_id' }

$pendingPath = if ($generationId) { Join-Path $pendingDir ($generationId + '.json') } else { $null }

if ($eventName -eq 'beforeSubmitPrompt') {
    $roots = @()
    if ($payload.workspace_roots) { $roots = @($payload.workspace_roots | ForEach-Object { [string]$_ }) }
    $pending = [ordered]@{
        time            = [DateTimeOffset]::UtcNow.ToString('o')
        conversationId  = $conversationId
        generationId    = $generationId
        prompt          = Get-Text $payload 'prompt'
        model           = Get-Text $payload 'model'
        workspace       = $(if ($roots.Count -gt 0) { $roots[0] } else { '' })
    }
    if ($pendingPath) {
        ($pending | ConvertTo-Json -Compress) | Set-Content -LiteralPath $pendingPath -Encoding UTF8
    }
    Write-Output '{"continue":true}'
    exit 0
}

if ($eventName -ne 'stop') {
    Write-Output '{}'
    exit 0
}

$started = [DateTimeOffset]::UtcNow
$prompt = ''
$workspace = ''
if ($pendingPath -and (Test-Path -LiteralPath $pendingPath)) {
    try {
        $pending = Get-Content -LiteralPath $pendingPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $prompt = Get-Text $pending 'prompt'
        $workspace = Get-Text $pending 'workspace'
        $startedText = Get-Text $pending 'time'
        $parsed = [DateTimeOffset]::MinValue
        if ($startedText -and [DateTimeOffset]::TryParse($startedText, [ref]$parsed)) { $started = $parsed }
    } catch {}
    Remove-Item -LiteralPath $pendingPath -Force -ErrorAction SilentlyContinue
}

$inputTokens = Get-Long $payload 'input_tokens'
$cacheRead = Get-Long $payload 'cache_read_tokens'
$cacheWrite = Get-Long $payload 'cache_write_tokens'
$outputTokens = Get-Long $payload 'output_tokens'
# В хуке input_tokens включает кэш — вычитаем, чтобы колонки не пересекались.
$uncached = $inputTokens - $cacheRead - $cacheWrite
if ($uncached -lt 0) { $uncached = [int64]0 }

$billed = $uncached + $outputTokens + $cacheRead + $cacheWrite
$durationMs = [int64]([DateTimeOffset]::UtcNow - $started).TotalMilliseconds
if ($durationMs -lt 0) { $durationMs = [int64]0 }

$record = [ordered]@{
    time           = [DateTimeOffset]::UtcNow.ToString('o')
    source         = 'hook'
    sessionId      = $conversationId
    messageId      = $generationId
    model          = Get-Text $payload 'model'
    prompt         = $prompt
    workspace      = $workspace
    status         = Get-Text $payload 'status'
    steps          = 1
    input          = $uncached
    output         = $outputTokens
    reasoning      = 0
    cacheRead      = $cacheRead
    cacheWrite     = $cacheWrite
    billed         = $billed
    context        = $inputTokens
    durationMs     = $durationMs
}

try {
    Add-Content -LiteralPath $eventsPath -Value (($record | ConvertTo-Json -Compress)) -Encoding UTF8
} catch {
    Add-Content -LiteralPath $logPath -Value ("{0:o} write error: {1}" -f [DateTimeOffset]::UtcNow, $_.Exception.Message)
}

Write-Output '{}'
exit 0
