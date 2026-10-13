param(
    [string]$CodexPath,
    [string]$CodexHome,
    [int]$TimeoutSeconds = 30
)
$ErrorActionPreference = 'Stop'
$probeProcess = $null
$probeError = $null
$probeSummary = $null
function Send-ProbeMessage($Message) {
    $probeProcess.StandardInput.WriteLine(($Message | ConvertTo-Json -Compress -Depth 12))
    $probeProcess.StandardInput.Flush()
}
function Read-ProbeResponse([int]$RequestId) {
    $probeDeadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $probeDeadline) {
        $probeRead = $probeProcess.StandardOutput.ReadLineAsync()
        $probeRemaining = [Math]::Max(1, [int]($probeDeadline - [DateTime]::UtcNow).TotalMilliseconds)
        if (-not $probeRead.Wait($probeRemaining)) { throw 'probe_timeout' }
        $probeLine = $probeRead.Result
        if ($null -eq $probeLine) { throw 'probe_disconnected' }
        try { $probeMessage = $probeLine | ConvertFrom-Json -ErrorAction Stop } catch { continue }
        if ($probeMessage.id -ne $RequestId) { continue }
        if ($null -ne $probeMessage.error) { throw ('rpc_error_' + $probeMessage.error.code) }
        return $probeMessage.result
    }
    throw 'probe_timeout'
}
function Convert-ProbeWindow($Window) {
    if ($null -eq $Window -or $null -eq $Window.usedPercent) { return $null }
    $probeUsed = [double]$Window.usedPercent
    return [ordered]@{
        used = $probeUsed
        remaining = [Math]::Min(100, [Math]::Max(0, 100 - $probeUsed))
        windowMinutes = $Window.windowDurationMins
        resetsAt = $Window.resetsAt
    }
}
try {
    if ([string]::IsNullOrWhiteSpace($CodexHome)) {
        $CodexHome = [Environment]::GetEnvironmentVariable('CODEX_HOME')
        if ([string]::IsNullOrWhiteSpace($CodexHome)) { $CodexHome = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.codex' }
    }
    if ([string]::IsNullOrWhiteSpace($CodexPath)) {
        $probeBin = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'OpenAI\Codex\bin'
        $probeExecutable = Get-ChildItem -LiteralPath $probeBin -Directory -ErrorAction SilentlyContinue | ForEach-Object { Get-Item -LiteralPath (Join-Path $_.FullName 'codex.exe') -ErrorAction SilentlyContinue } | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
        if ($null -ne $probeExecutable) { $CodexPath = $probeExecutable.FullName }
        else {
            $probeCommand = Get-Command codex.exe -ErrorAction SilentlyContinue
            if ($null -ne $probeCommand) { $CodexPath = $probeCommand.Source }
        }
    }
    if ([string]::IsNullOrWhiteSpace($CodexPath)) { throw 'probe_codex_not_found' }
    $probeStart = New-Object System.Diagnostics.ProcessStartInfo
    $probeStart.FileName = $CodexPath
    $probeStart.Arguments = 'app-server --listen stdio://'
    $probeStart.UseShellExecute = $false
    $probeStart.CreateNoWindow = $true
    $probeStart.RedirectStandardInput = $true
    $probeStart.RedirectStandardOutput = $true
    $probeStart.RedirectStandardError = $true
    $probeStart.StandardOutputEncoding = New-Object System.Text.UTF8Encoding($false)
    $probeStart.EnvironmentVariables['CODEX_HOME'] = $CodexHome
    $probeProcess = New-Object System.Diagnostics.Process
    $probeProcess.StartInfo = $probeStart
    if (-not $probeProcess.Start()) { throw 'probe_start_failed' }
    $probeProcess.BeginErrorReadLine()
    Send-ProbeMessage @{ id = 1; method = 'initialize'; params = @{ clientInfo = @{ name = 'codex_quota_probe'; title = 'Codex Quota Probe'; version = '0.1.0' } } }
    $null = Read-ProbeResponse 1
    Send-ProbeMessage @{ method = 'initialized'; params = @{} }
    Send-ProbeMessage @{ id = 2; method = 'account/read'; params = @{ refreshToken = $false } }
    $probeAccountResult = Read-ProbeResponse 2
    $probeAccount = $probeAccountResult.account
    $probeAuth = if ($null -eq $probeAccount) { 'signedOut' } else { [string]$probeAccount.type }
    $probeFingerprint = $null
    if ($null -ne $probeAccount) {
        $probeIdentity = @($probeAccount.chatgptAccountId, $probeAccount.accountId, $probeAccount.id, $probeAccount.email) | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } | Select-Object -First 1
        if ($null -ne $probeIdentity) {
            $probeHash = [System.Security.Cryptography.SHA256]::Create()
            try { $probeFingerprint = ([BitConverter]::ToString($probeHash.ComputeHash([Text.Encoding]::UTF8.GetBytes([string]$probeIdentity))).Replace('-', '').ToLowerInvariant()).Substring(0, 12) } finally { $probeHash.Dispose() }
        }
    }
    if ($probeAuth -ne 'chatgpt' -and $probeAuth -ne 'chatgptAuthTokens') { throw 'probe_chatgpt_login_required' }
    Send-ProbeMessage @{ id = 3; method = 'account/rateLimits/read' }
    $probeLimitsResult = Read-ProbeResponse 3
    $probeBucket = $probeLimitsResult.rateLimitsByLimitId.codex
    if ($null -eq $probeBucket) {
        $probeLegacy = $probeLimitsResult.rateLimits
        if ($null -ne $probeLegacy -and ($probeLegacy.limitId -eq 'codex' -or ($null -eq $probeLimitsResult.rateLimitsByLimitId -and [string]::IsNullOrEmpty([string]$probeLegacy.limitId)))) { $probeBucket = $probeLegacy }
    }
    $probeWindows = @($probeBucket.primary, $probeBucket.secondary)
    $probeFiveHour = $probeWindows | Where-Object { $null -ne $_ -and $_.windowDurationMins -eq 300 } | Select-Object -First 1
    $probeWeekly = $probeWindows | Where-Object { $null -ne $_ -and $_.windowDurationMins -eq 10080 } | Select-Object -First 1
    $probeSummary = [ordered]@{ status = 'ok'; authType = $probeAuth; accountFingerprint = $probeFingerprint; fiveHour = (Convert-ProbeWindow $probeFiveHour); weekly = (Convert-ProbeWindow $probeWeekly) }
} catch {
    $probeError = if ($_.Exception.Message -match '^(probe_[a-z_]+|rpc_error_-?\d+)$') { $_.Exception.Message } else { 'probe_failed' }
    $probeSummary = [ordered]@{ status = 'error'; error = $probeError }
} finally {
    if ($null -ne $probeProcess) {
        try {
            if (-not $probeProcess.HasExited) {
                $probeProcess.StandardInput.Close()
                if (-not $probeProcess.WaitForExit(3000)) {
                    $probeKill = New-Object System.Diagnostics.ProcessStartInfo
                    $probeKill.FileName = Join-Path $env:SystemRoot 'System32\taskkill.exe'
                    $probeKill.Arguments = '/PID ' + $probeProcess.Id + ' /T /F'
                    $probeKill.UseShellExecute = $false
                    $probeKill.CreateNoWindow = $true
                    $probeKill.RedirectStandardOutput = $true
                    $probeKill.RedirectStandardError = $true
                    $probeKiller = [System.Diagnostics.Process]::Start($probeKill)
                    $null = $probeKiller.WaitForExit(5000)
                    $probeKiller.Dispose()
                }
            }
        } catch {}
        $probeProcess.Dispose()
    }
}
$probeSummary | ConvertTo-Json -Compress -Depth 8
if ($null -ne $probeError) { exit 1 }
