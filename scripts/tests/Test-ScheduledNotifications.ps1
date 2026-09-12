#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
# Fixed synthetic addresses/keys only. No private provider calls.
$Api = 'http://127.0.0.1:15100'
$Checks = 0
function Check([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
    $script:Checks++
    Write-Host "PASS: $Message"
}
function Call([string] $Path, [string] $Method = 'GET', $Body = $null, [string] $Actor = 'demo-owner') {
    $Deadline = [DateTimeOffset]::UtcNow.AddSeconds(90)
    do {
        $Timestamp = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds().ToString()
        $Signature = [Convert]::ToHexString([Security.Cryptography.HMACSHA256]::HashData([Text.Encoding]::UTF8.GetBytes('synthetic-local-actor-signing-key'), [Text.Encoding]::UTF8.GetBytes("$Actor`nOwner`n`n$Timestamp")))
        $Parameters = @{ Uri = "$Api$Path"; Method = $Method; SkipHttpErrorCheck = $true; TimeoutSec = 45; Headers = @{
            'X-Internal-Api-Key' = 'synthetic-local-internal-key'; 'X-Agent-Actor' = $Actor; 'X-Agent-Role' = 'Owner'
            'X-Agent-Timestamp' = $Timestamp; 'X-Agent-Signature' = $Signature
        } }
        if ($null -ne $Body) { $Parameters.Body = $Body | ConvertTo-Json -Depth 8; $Parameters.ContentType = 'application/json' }
        $Response = Invoke-WebRequest @Parameters
        if ($Response.StatusCode -ne 429) { return $Response }
        if ([DateTimeOffset]::UtcNow -ge $Deadline) { throw 'Synthetic API rate limit did not clear' }
        Start-Sleep -Seconds 2
    } while ($true)
}
function Schedule([string] $Delay) {
    $Response = Call '/api/schedule/notifications' POST @{tenantId='default';userId='demo-owner';title='Synthetic scheduled reminder';body='Review the notification dashboard';delay=$Delay}
    Check ($Response.StatusCode -eq 200) 'Signed notification creation succeeds'
    ($Response.Content | ConvertFrom-Json).id
}
function Wait-Job([string] $Id) {
    $Deadline = [DateTimeOffset]::UtcNow.AddSeconds(90)
    do {
        $Response = Call "/api/jobs/$Id"
        if ($Response.StatusCode -ne 200) { throw "Cannot inspect notification job: $($Response.StatusCode)" }
        $Detail = $Response.Content | ConvertFrom-Json
        if ($Detail.job.status -in @('Completed','Failed','Blocked','NeedsReview','Cancelled')) { return $Detail }
        Start-Sleep -Seconds 2
    } while ([DateTimeOffset]::UtcNow -lt $Deadline)
    throw 'Notification did not reach an outcome'
}
Check ((Call '/api/models').Content.Contains('synthetic-demo')) 'Target is the synthetic API'
$Id = Schedule 'PT8S'
$Jobs = (Call '/api/jobs/?profileId=demo-owner').Content | ConvertFrom-Json
$Job = $Jobs | Where-Object taskId -eq $Id
Check ($Job.jobType -eq 'Notification' -and $Job.actorId -eq 'demo-owner' -and $Job.notification.body -eq 'Review the notification dashboard') 'Notification appears immediately with scheduler and body'
docker compose -f compose.synthetic.yml restart personalagent-worker | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Synthetic Worker restart failed' }
$Detail = Wait-Job $Id
Check ($Detail.job.status -eq 'Failed' -and $Detail.job.outcome.Contains('disabled') -and $Detail.attempts.Count -eq 1) 'Worker restart preserves the job and records disabled push honestly'
Check ($null -eq $Detail.job.sessionId) 'Notification does not create an agent conversation'
$Replay = Call "/api/jobs/$Id/execute" POST @{taskId=$Id;tenantId='forged';userId='other';instruction='Replace reminder';executeAtUtc=[DateTimeOffset]::UtcNow;correlationId=[guid]::NewGuid()}
Check ($Replay.StatusCode -eq 200 -and ($Replay.Content | ConvertFrom-Json).status -eq 'Failed') 'Forged duplicate delivery returns the stored outcome'
Check (((Call "/api/jobs/$Id").Content | ConvertFrom-Json).attempts.Count -eq 1) 'Replay creates no second delivery attempt'
Check ((Call "/api/jobs/$Id" -Actor 'demo-other').StatusCode -eq 404) 'Another owner cannot read the reminder'
$CancelId = Schedule 'PT5M'
Check ((Call "/api/jobs/$CancelId/cancel" POST).StatusCode -eq 204) 'Pending reminder can be cancelled'
Check ((Wait-Job $CancelId).job.status -eq 'Cancelled') 'Notification cancellation persists'
$Permissions = ((Call '/api/admin/tool-access').Content | ConvertFrom-Json).permissions
$BlockedId = Schedule 'PT8S'
try {
    $Changed = $Permissions | ConvertTo-Json -Depth 8 | ConvertFrom-Json
    $Permission = $Changed | Where-Object { $_.role -eq 'Owner' -and $_.toolKey -eq 'Local:schedule_notification' }
    if ($null -eq $Permission) { throw 'Notification permission key not found' }
    $Permission.isEnabled = $false
    Check ((Call '/api/admin/tool-access' PUT @{permissions=$Changed;updatedBy='demo-owner'}).StatusCode -eq 204) 'Notification permission revoked after creation'
    Check ((Wait-Job $BlockedId).job.status -eq 'Blocked') 'Revoked notification never reaches push delivery'
} finally {
    if ((Call '/api/admin/tool-access' PUT @{permissions=$Permissions;updatedBy='demo-owner'}).StatusCode -ne 204) { throw 'Failed to restore synthetic permissions' }
}
Write-Host "Scheduled notifications: $Checks checks passed. Job: $Id"
