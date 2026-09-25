#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$Api = 'http://127.0.0.1:15100'
$Checks = 0
function Check([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
    $script:Checks++
    Write-Host "PASS: $Message"
}
function Call([string] $Path, [string] $Method = 'GET', $Body = $null, [string] $Actor = 'demo-owner') {
    $Timestamp = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds().ToString()
    $Signature = [Convert]::ToHexString([Security.Cryptography.HMACSHA256]::HashData([Text.Encoding]::UTF8.GetBytes('synthetic-local-actor-signing-key'), [Text.Encoding]::UTF8.GetBytes("$Actor`nOwner`n`n$Timestamp")))
    $Parameters = @{ Uri = "$Api$Path"; Method = $Method; SkipHttpErrorCheck = $true; TimeoutSec = 45; Headers = @{
        'X-Internal-Api-Key' = 'synthetic-local-internal-key'; 'X-Agent-Actor' = $Actor; 'X-Agent-Role' = 'Owner'
        'X-Agent-Email' = ''; 'X-Agent-Timestamp' = $Timestamp; 'X-Agent-Signature' = $Signature
    } }
    if ($null -ne $Body) { $Parameters.Body = $Body | ConvertTo-Json -Depth 10; $Parameters.ContentType = 'application/json' }
    Invoke-WebRequest @Parameters
}
function Wait-Run([string] $Id) {
    $Until = [DateTimeOffset]::UtcNow.AddSeconds(60)
    do {
        $Detail = (Call "/api/automations/$Id`?profileId=demo-owner").Content | ConvertFrom-Json
        $Run = $Detail.runs | Where-Object status -eq 'Completed' | Select-Object -First 1
        if ($Run) { return $Run }
        Start-Sleep -Milliseconds 500
    } while ([DateTimeOffset]::UtcNow -lt $Until)
    throw 'Automation did not complete within 60 seconds'
}
Check ((Call '/api/models').Content.Contains('synthetic-demo')) 'Target is the isolated synthetic API'
$Opened = Call '/api/conversation/open' POST @{profileId='demo-owner';modelId='synthetic-demo'}
Check ($Opened.StatusCode -eq 200) 'Conversation opened'
$Session = ($Opened.Content | ConvertFrom-Json).sessionId
if (-not $Session) { $Session = ($Opened.Content | ConvertFrom-Json).id }
$Reply = Call "/api/conversation/$Session/messages" POST @{profileId='demo-owner';message='demo automation'}
Check ($Reply.StatusCode -eq 200) 'Chat invoked the real automation creation tool'
$List = (Call '/api/automations/?profileId=demo-owner').Content | ConvertFrom-Json
$Automation = $List | Where-Object name -eq 'Synthetic chat automation' | Select-Object -First 1
Check ($null -ne $Automation) 'Chat-authored automation is listed'
$Id = $Automation.id
$Run = Wait-Run $Id
$Result = (Call "/api/automations/$Id/runs/$($Run.id)?profileId=demo-owner").Content | ConvertFrom-Json
Check ($Result.reports.Count -eq 1 -and $Result.reports[0].content -eq 'Created through chat') 'SQL saga commits one report and completed steps'
$Detail = (Call "/api/automations/$Id`?profileId=demo-owner").Content | ConvertFrom-Json
Check ($Detail.upcomingRuns.Count -eq 5 -and $Detail.versions.Count -eq 1) 'Recurring schedule and source revision are visible'
Check ((Call "/api/automations/$Id`?profileId=demo-owner" -Actor 'demo-other').StatusCode -eq 403) 'Another owner cannot inspect private automation source'
Check ((Call "/api/automations/$Id/pause?profileId=demo-owner" POST).StatusCode -eq 204) 'Future executions can be paused'
$Revision = Call "/api/automations/$Id`?profileId=demo-owner" PUT @{name='Revised synthetic automation'; source=$Detail.versions[0].source; expectedVersion=1; executeAt=[DateTimeOffset]::UtcNow.AddHours(1);repeatEvery='P1D'}
Check ($Revision.StatusCode -eq 200) 'New immutable revision is accepted'
$Stale = Call "/api/automations/$Id`?profileId=demo-owner" PUT @{name='Stale';source=$Detail.versions[0].source;expectedVersion=1}
Check ($Stale.StatusCode -eq 409) 'Stale revision cannot replace newer source'
$Detail = (Call "/api/automations/$Id`?profileId=demo-owner").Content | ConvertFrom-Json
Check ($Detail.versions.Count -eq 2 -and $Detail.runs[0].version -eq 1) 'Historical runs retain their original version'
Call "/api/automations/$Id/pause?profileId=demo-owner" POST | Out-Null
Write-Host "Automations: $Checks checks passed. Dashboard: http://127.0.0.1:15000/automations?automationId=$Id"
