#requires -Version 7.0
[CmdletBinding()]
param([switch] $RestartWorker)
$ErrorActionPreference = 'Stop'
$Api = 'http://127.0.0.1:15100/api/admin/integrations/sentry'
$Checks = 0

function Assert-Integration([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
    $script:Checks++
    Write-Host "PASS: $Message"
}

function Invoke-Settings([string] $Method = 'GET', [string] $Suffix = '', $Body = $null, [string] $Actor = 'demo-owner') {
    $Deadline = [DateTimeOffset]::UtcNow.AddSeconds(90)
    $RateLimited = $false
    do {
        $Timestamp = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds().ToString()
        $Signature = [Convert]::ToHexString([Security.Cryptography.HMACSHA256]::HashData(
            [Text.Encoding]::UTF8.GetBytes('synthetic-local-actor-signing-key'), [Text.Encoding]::UTF8.GetBytes("$Actor`nOwner`n`n$Timestamp")))
        $Parameters = @{
            Uri = "$Api$Suffix"; Method = $Method; SkipHttpErrorCheck = $true; TimeoutSec = 30
            Headers = @{ 'X-Internal-Api-Key' = 'synthetic-local-internal-key'; 'X-Agent-Actor' = $Actor
                'X-Agent-Role' = 'Owner'; 'X-Agent-Timestamp' = $Timestamp; 'X-Agent-Signature' = $Signature }
        }
        if ($null -ne $Body) { $Parameters.Body = $Body | ConvertTo-Json -Depth 8; $Parameters.ContentType = 'application/json' }
        $Response = Invoke-WebRequest @Parameters
        if ($Response.StatusCode -ne 429) { return $Response }
        # The shared fixed-window limiter rejects before endpoint execution, so replaying
        # this request is safe. Do not retry conflicts, validation errors or server failures.
        if ([DateTimeOffset]::UtcNow -ge $Deadline) { throw "Integration settings $Method $Suffix remained rate-limited (HTTP 429) for 90 seconds." }
        if (-not $RateLimited) { Write-Host 'API rate limit reached; waiting for the next request window.'; $RateLimited = $true }
        Start-Sleep -Seconds 2
    } while ($true)
}

function Wait-Applied([long] $Revision) {
    $Deadline = [DateTimeOffset]::UtcNow.AddSeconds(60)
    do {
        $Response = Invoke-Settings
        if ($Response.StatusCode -ne 200) { throw "Reading integration acknowledgements failed (HTTP $($Response.StatusCode))." }
        $State = $Response.Content | ConvertFrom-Json
        $Current = @($State.instances | Where-Object { $_.revision -eq $Revision -and $_.status -eq 'Disabled' -and [DateTimeOffset]$_.lastSeen -gt [DateTimeOffset]::UtcNow.AddSeconds(-30) })
        if (@($Current.service | Sort-Object -Unique).Count -eq 3) { return $State }
        Start-Sleep -Seconds 1
    } while ([DateTimeOffset]::UtcNow -lt $Deadline)
    throw 'All three services did not acknowledge the disabled revision in time.'
}

$Initial = Invoke-Settings
Assert-Integration ($Initial.StatusCode -eq 200) 'Deployment administrator can load integration settings'
$InitialState = $Initial.Content | ConvertFrom-Json
if ($InitialState.values.Enabled -ne 'false' -or $InitialState.configuredSecrets.Count -gt 0) {
    throw 'This smoke test requires disabled Sentry with no saved DSN in the synthetic environment; it will not overwrite configured credentials.'
}
Assert-Integration ((Invoke-Settings -Actor 'demo-other').StatusCode -eq 403) 'Another owner cannot access deployment settings'
$Dsn = 'https://0123456789abcdef0123456789abcdef@o123.ingest.us.sentry.io/456'
$Saved = Invoke-Settings -Method PUT -Body @{ expectedRevision = $InitialState.savedRevision; values = @{ Enabled = 'false'; Dsn = $Dsn; Environment = 'synthetic-smoke'; SampleRate = '1' } }
Assert-Integration ($Saved.StatusCode -eq 200 -and -not $Saved.Content.Contains($Dsn)) 'Save validates and stores the synthetic DSN without echoing it'
$SavedState = $Saved.Content | ConvertFrom-Json
Assert-Integration ($SavedState.configuredSecrets -contains 'Dsn' -and $SavedState.activeRevision -eq $InitialState.activeRevision) 'Saved secret is configured while active revision remains unchanged'
$Stale = Invoke-Settings -Method PUT -Body @{ expectedRevision = $InitialState.savedRevision; values = @{ Environment = 'stale' } }
Assert-Integration ($Stale.StatusCode -eq 409) 'Stale edits are rejected'
$Invalid = Invoke-Settings -Method PUT -Body @{ expectedRevision = $SavedState.savedRevision; values = @{ Dsn = 'http://127.0.0.1/private' } }
Assert-Integration ($Invalid.StatusCode -eq 400 -and -not $Invalid.Content.Contains('http://127.0.0.1/private')) 'Unsafe DSN rejected without echoing input'
$Applied = Invoke-Settings -Method POST -Suffix '/apply' -Body @{ revision = $SavedState.savedRevision }
Assert-Integration ($Applied.StatusCode -eq 200) 'Apply promotes the reviewed revision'
$AppliedState = Wait-Applied $SavedState.savedRevision
Assert-Integration ($AppliedState.activeRevision -eq $SavedState.savedRevision) 'API, Web, and Worker acknowledge the applied revision'
$Test = Invoke-Settings -Method POST -Suffix '/test' -Body @{ revision = $SavedState.savedRevision }
Assert-Integration ($Test.StatusCode -eq 200 -and ($Test.Content | ConvertFrom-Json).eventId -eq '') 'Disabled reporting queues no test event'

if ($RestartWorker) {
    $PreviousInstances = @($AppliedState.instances | Where-Object service -eq 'Worker' | ForEach-Object instance)
    docker compose -f (Join-Path $PSScriptRoot '../../compose.synthetic.yml') restart personalagent-worker
    if ($LASTEXITCODE -ne 0) { throw 'Synthetic Worker restart failed.' }
    $Deadline = [DateTimeOffset]::UtcNow.AddSeconds(60)
    do {
        $Response = Invoke-Settings
        if ($Response.StatusCode -ne 200) { throw "Reading restarted Worker acknowledgement failed (HTTP $($Response.StatusCode))." }
        $AfterRestart = $Response.Content | ConvertFrom-Json
        $NewWorker = @($AfterRestart.instances | Where-Object { $_.service -eq 'Worker' -and $_.instance -notin $PreviousInstances -and $_.revision -eq $SavedState.savedRevision })
        if ($NewWorker.Count -gt 0) { break }
        Start-Sleep -Seconds 1
    } while ([DateTimeOffset]::UtcNow -lt $Deadline)
    Assert-Integration ($NewWorker.Count -gt 0) 'A restarted Worker loads the active revision without another apply'
}

$Cleared = Invoke-Settings -Method PUT -Body @{ expectedRevision = $SavedState.savedRevision; values = @{ Enabled = 'false'; Dsn = ''; Environment = $InitialState.values.Environment; SampleRate = $InitialState.values.SampleRate } }
Assert-Integration ($Cleared.StatusCode -eq 200) 'Clear removes the configured secret from the next revision'
$ClearedState = $Cleared.Content | ConvertFrom-Json
Assert-Integration ($ClearedState.configuredSecrets.Count -eq 0) 'Read responses show the secret as missing after clear'
$Restore = Invoke-Settings -Method POST -Suffix '/apply' -Body @{ revision = $ClearedState.savedRevision }
Assert-Integration ($Restore.StatusCode -eq 200) "Restore the initial disabled settings as a new revision (HTTP $($Restore.StatusCode))"
$null = Wait-Applied $ClearedState.savedRevision
Write-Host "Integration settings: $Checks checks passed. No events were sent to Sentry."
