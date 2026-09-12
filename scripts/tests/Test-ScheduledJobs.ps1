#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
# Fixed local synthetic endpoints and credentials; never points at a production instance.
$Api = 'http://127.0.0.1:15100'
$Checks = 0
function Check([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
    $script:Checks++
    Write-Host "PASS: $Message"
}
function Call([string] $Path, [string] $Method = 'GET', $Body = $null, [string] $Actor = 'demo-owner', [string] $Role = 'Owner', [string] $Email = '') {
    $Timestamp = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds().ToString()
    $Signature = [Convert]::ToHexString([Security.Cryptography.HMACSHA256]::HashData([Text.Encoding]::UTF8.GetBytes('synthetic-local-actor-signing-key'), [Text.Encoding]::UTF8.GetBytes("$Actor`n$Role`n$Email`n$Timestamp")))
    $Parameters = @{ Uri = "$Api$Path"; Method = $Method; SkipHttpErrorCheck = $true; TimeoutSec = 45; Headers = @{
        'X-Internal-Api-Key' = 'synthetic-local-internal-key'; 'X-Agent-Actor' = $Actor; 'X-Agent-Role' = $Role
        'X-Agent-Email' = $Email; 'X-Agent-Timestamp' = $Timestamp; 'X-Agent-Signature' = $Signature
    } }
    if ($null -ne $Body) { $Parameters.Body = $Body | ConvertTo-Json -Depth 8; $Parameters.ContentType = 'application/json' }
    Invoke-WebRequest @Parameters
}
function Schedule([string] $Delay, [string] $Instruction) {
    $Response = Call '/api/schedule/agent-tasks' POST @{tenantId='default'; userId='demo-owner'; instruction=$Instruction; delay=$Delay}
    Check ($Response.StatusCode -eq 200) 'Authorized job persisted'
    ($Response.Content | ConvertFrom-Json).id
}
function Wait-Job([string] $Id) {
    $Until = [DateTimeOffset]::UtcNow.AddSeconds(60)
    do {
        $Response = Call "/api/jobs/$Id"
        if ($Response.StatusCode -ne 200) { throw "Cannot inspect job: $($Response.StatusCode)" }
        $Detail = $Response.Content | ConvertFrom-Json
        if ($Detail.job.status -in @('Completed','Failed','Blocked','NeedsReview','Cancelled')) { return $Detail }
        Start-Sleep -Milliseconds 700
    } while ([DateTimeOffset]::UtcNow -lt $Until)
    throw 'Scheduled job did not reach an outcome within 60 seconds'
}
Check ((Call '/api/models').Content.Contains('synthetic-demo')) 'Target is the synthetic API'
$Id = Schedule 'PT8S' 'Summarize my synthetic garden notes'
docker compose -f compose.synthetic.yml restart personalagent-worker | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Synthetic Worker restart failed' }
$Detail = Wait-Job $Id
Check ($Detail.job.status -eq 'Completed' -and $Detail.job.actorId -eq 'demo-owner' -and $Detail.attempts.Count -eq 1) 'Worker restart preserves identity and completes one attempt'
$Replay = Call "/api/jobs/$Id/execute" POST @{taskId=$Id;tenantId='forged';userId='other';instruction='Replace stored work';executeAtUtc=[DateTimeOffset]::UtcNow;correlationId=[guid]::NewGuid()}
Check ($Replay.StatusCode -eq 200 -and ($Replay.Content | ConvertFrom-Json).status -eq 'Completed') 'Duplicate delivery returns the saved outcome'
Check (((Call "/api/jobs/$Id").Content | ConvertFrom-Json).attempts.Count -eq 1) 'Replay does not create another attempt'
Check ((Call "/api/jobs/$Id" -Actor 'demo-other').StatusCode -eq 404) 'Other owner cannot inspect the job'
Check ((Call '/api/jobs/?profileId=demo-owner' -Actor 'demo-other').StatusCode -eq 403) 'Other owner cannot list the subject jobs'
$CancelId = Schedule 'PT5M' 'Pending cancellation demonstration'
Check ((Call "/api/jobs/$CancelId/cancel" POST).StatusCode -eq 204) 'Pending cancellation succeeds'
Check ((Wait-Job $CancelId).job.status -eq 'Cancelled') 'Cancelled state is persisted'
$Catalog = (Call '/api/admin/tool-access').Content | ConvertFrom-Json
$Permissions = $Catalog.permissions
$BlockedId = Schedule 'PT8S' 'Revocation demonstration'
try {
    $Changed = $Permissions | ConvertTo-Json -Depth 8 | ConvertFrom-Json
    $Permission = $Changed | Where-Object { $_.role -eq 'Owner' -and $_.toolKey -eq 'Local:schedule_agent_task' }
    if ($null -eq $Permission) { throw 'Scheduling permission key not found' }
    $Permission.isEnabled = $false
    $Saved = Call '/api/admin/tool-access' PUT @{permissions=$Changed;updatedBy='demo-owner'}
    Check ($Saved.StatusCode -eq 204) 'Scheduling permission revoked after creation'
    $Blocked = Wait-Job $BlockedId
    Check ($Blocked.job.status -eq 'Blocked' -and $null -eq $Blocked.job.sessionId) 'Revoked job records a denial without executing'
} finally {
    $Restored = Call '/api/admin/tool-access' PUT @{permissions=$Permissions;updatedBy='demo-owner'}
    if ($Restored.StatusCode -ne 204) { throw 'Failed to restore synthetic permissions' }
}
Write-Host "Scheduled jobs: $Checks checks passed. Completed job: $Id"

