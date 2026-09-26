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
function Call([string] $Path, [string] $Method = 'GET', $Body = $null) {
    $Deadline = [DateTimeOffset]::UtcNow.AddSeconds(90)
    $RateLimited = $false
    do {
        $Timestamp = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds().ToString()
        $Signature = [Convert]::ToHexString([Security.Cryptography.HMACSHA256]::HashData([Text.Encoding]::UTF8.GetBytes('synthetic-local-actor-signing-key'), [Text.Encoding]::UTF8.GetBytes("demo-owner`nOwner`n`n$Timestamp")))
        $Parameters = @{ Uri = "$Api$Path"; Method = $Method; SkipHttpErrorCheck = $true; TimeoutSec = 45; Headers = @{
            'X-Internal-Api-Key' = 'synthetic-local-internal-key'; 'X-Agent-Actor' = 'demo-owner'; 'X-Agent-Role' = 'Owner'
            'X-Agent-Email' = ''; 'X-Agent-Timestamp' = $Timestamp; 'X-Agent-Signature' = $Signature
        } }
        if ($null -ne $Body) { $Parameters.Body = $Body | ConvertTo-Json -Depth 12; $Parameters.ContentType = 'application/json' }
        $Response = Invoke-WebRequest @Parameters
        if ($Response.StatusCode -ne 429) { return $Response }
        # The shared limiter rejects before endpoint execution; retry only HTTP 429.
        if ([DateTimeOffset]::UtcNow -ge $Deadline) { throw "Automation smoke request $Method $Path remained rate-limited (HTTP 429) for 90 seconds." }
        if (-not $RateLimited) { Write-Host 'API rate limit reached; waiting for the next request window.'; $RateLimited = $true }
        Start-Sleep -Seconds 2
    } while ($true)
}
function Wait-Run([string] $Id, [string] $Status) {
    $Until = [DateTimeOffset]::UtcNow.AddSeconds(120)
    do {
        $Detail = (Call "/api/automations/$Id`?profileId=demo-owner").Content | ConvertFrom-Json
        $Run = $Detail.runs | Where-Object status -eq $Status | Select-Object -First 1
        if ($Run) { return (Call "/api/automations/$Id/runs/$($Run.id)?profileId=demo-owner").Content | ConvertFrom-Json }
        Start-Sleep -Milliseconds 500
    } while ([DateTimeOffset]::UtcNow -lt $Until)
    throw "Program did not reach $Status within 120 seconds"
}
Check ((Call '/api/models').Content.Contains('synthetic-demo')) 'Target is the isolated synthetic API'
$Catalog = (Call '/api/admin/tool-access').Content | ConvertFrom-Json
$Original = ($Catalog.permissions | Where-Object { $_.role -eq 'Owner' -and $_.toolKey -eq 'Local:csharp_automation' }).isEnabled
function Set-ProgramPermission([bool] $Enabled) {
    $Response = Call '/api/admin/tool-access' PUT @{permissions=@(@{role='Owner';toolKey='Local:csharp_automation';isEnabled=$Enabled});updatedBy='demo-owner'}
    Check ($Response.StatusCode -eq 204) "C# execution permission set to $Enabled"
}
try {
    Set-ProgramPermission $false
    $Source = '{"steps":[{"id":"program","action":"csharp","arguments":{"source":"Console.Write(1);","input":""}}]}'
    Check ((Call '/api/automations/?profileId=demo-owner' POST @{name='Denied program';source=$Source}).StatusCode -eq 403) 'Disabled program capability denies creation'
    Set-ProgramPermission $true
    $Session = ((Call '/api/conversation/open' POST @{profileId='demo-owner';modelId='synthetic-demo'}).Content | ConvertFrom-Json).sessionId
    $Reply = Call "/api/conversation/$Session/messages" POST @{profileId='demo-owner';message='demo csharp automation'}
    Check ($Reply.StatusCode -eq 200) 'Chat invoked C# automation authoring'
    $Id = ((Call '/api/automations/?profileId=demo-owner').Content | ConvertFrom-Json | Where-Object name -eq 'Synthetic C# automation' | Select-Object -First 1).id
    Check ($null -ne $Id) 'Versioned C# source is listed'
    $Result = Wait-Run $Id 'Completed'
    Check ($Result.reports.Count -eq 1 -and $Result.reports[0].content -eq 'CREATED BY C#') 'Isolated C# stdout flows into transactional report'
    $Evidence = $Result.steps[0].programEvidence | ConvertFrom-Json
    Check ($Evidence.ImageId.StartsWith('sha256:') -and $Evidence.SourceHash.Length -eq 64 -and $Evidence.ExitCode -eq 0) 'Compiler image, source hash and exit status are persisted'
    Call "/api/automations/$Id/pause?profileId=demo-owner" POST | Out-Null
    $Bad = ((Call '/api/automations/?profileId=demo-owner' POST @{name='Synthetic compile failure';source=$Source.Replace('Console.Write(1);','not valid C#')}).Content | ConvertFrom-Json).id
    $Failure = Wait-Run $Bad 'Failed'
    Check ($Failure.steps[0].programEvidence.Contains('BuildFailed') -and $Failure.steps[0].programEvidence.Contains('error CS')) 'Build errors appear in scoped run diagnostics'
    Write-Host "C# automations: $Checks checks passed. Dashboard: http://127.0.0.1:15000/automations?automationId=$Id"
}
finally { Set-ProgramPermission ([bool]$Original) }
