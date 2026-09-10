#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$Api = 'http://127.0.0.1:15100'
$Web = 'http://127.0.0.1:15000'
$Checks = 0

function Assert-Demo([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
    $script:Checks++
    Write-Host "PASS: $Message"
}

function Invoke-DemoApi([string] $Path, [string] $Actor = 'demo-owner', [string] $Role = 'Owner', [string] $Email = '', [string] $Method = 'GET', $Body = $null) {
    $Timestamp = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds().ToString()
    $Payload = "$Actor`n$Role`n$Email`n$Timestamp"
    $Signature = [Convert]::ToHexString([System.Security.Cryptography.HMACSHA256]::HashData(
        [Text.Encoding]::UTF8.GetBytes('synthetic-local-actor-signing-key'), [Text.Encoding]::UTF8.GetBytes($Payload)))
    $Parameters = @{
        Uri = "$Api$Path"; Method = $Method; SkipHttpErrorCheck = $true; TimeoutSec = 45
        Headers = @{
            'X-Internal-Api-Key' = 'synthetic-local-internal-key'; 'X-Agent-Actor' = $Actor
            'X-Agent-Role' = $Role; 'X-Agent-Email' = $Email; 'X-Agent-Timestamp' = $Timestamp; 'X-Agent-Signature' = $Signature
        }
    }
    if ($null -ne $Body) { $Parameters.Body = $Body | ConvertTo-Json -Depth 8; $Parameters.ContentType = 'application/json' }
    Invoke-WebRequest @Parameters
}

Assert-Demo ((Invoke-WebRequest "$Api/health/ready").StatusCode -eq 200) 'API ready after schema and synthetic seed initialization'
Assert-Demo ((Invoke-WebRequest "$Web/health/ready").StatusCode -eq 200) 'Web ready after encrypted configuration loading'
$Models = Invoke-DemoApi '/api/models'
Assert-Demo ($Models.StatusCode -eq 200 -and ($Models.Content | ConvertFrom-Json).models[0].id -eq 'synthetic-demo') 'Synthetic model catalog and decrypted internal credential'
$Unsigned = Invoke-WebRequest "$Api/api/sessions?profileId=demo-owner" -Headers @{ 'X-Internal-Api-Key' = 'synthetic-local-internal-key' } -SkipHttpErrorCheck
Assert-Demo ($Unsigned.StatusCode -eq 401) 'Unsigned actor rejected'
$Denied = Invoke-DemoApi '/api/sessions?profileId=demo-other'
Assert-Demo ($Denied.StatusCode -eq 403) 'Owner cannot access another subject'
$Assigned = Invoke-DemoApi '/api/coach-checkins?profileId=demo-owner' 'google:synthetic-coach' 'Coach' 'coach@example.test'
Assert-Demo ($Assigned.StatusCode -eq 200) 'Assigned coach can read the synthetic athlete'
$Unassigned = Invoke-DemoApi '/api/coach-checkins?profileId=demo-other' 'google:synthetic-coach' 'Coach' 'coach@example.test'
Assert-Demo ($Unassigned.StatusCode -eq 403) 'Coach cannot access an unassigned athlete'
$Created = Invoke-DemoApi -Path '/api/sessions' -Method POST -Body @{ profileId = 'demo-owner' }
Assert-Demo ($Created.StatusCode -eq 200) 'Create a real stored chat session'
$SessionId = ($Created.Content | ConvertFrom-Json).sessionId
$Reply = Invoke-DemoApi -Path "/api/sessions/$SessionId/messages" -Method POST -Body @{ profileId = 'demo-owner'; message = 'What is my favorite exercise?' }
Assert-Demo ($Reply.StatusCode -eq 200 -and $Reply.Content.Contains('deadlift')) 'Chat recalls the seeded account memory through pgvector'
$OtherHistory = Invoke-DemoApi "/api/sessions/$SessionId/messages?profileId=demo-other" 'demo-other'
Assert-Demo ($OtherHistory.StatusCode -eq 404) 'Another owner cannot read the new conversation'

# Exercise the actual upload endpoint and MassTransit API -> Worker -> API path.
$Timestamp = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds().ToString()
$Signature = [Convert]::ToHexString([Security.Cryptography.HMACSHA256]::HashData(
    [Text.Encoding]::UTF8.GetBytes('synthetic-local-actor-signing-key'), [Text.Encoding]::UTF8.GetBytes("demo-owner`nOwner`n`n$Timestamp")))
$Client = [Net.Http.HttpClient]::new()
$Form = [Net.Http.MultipartFormDataContent]::new()
try {
    $Client.DefaultRequestHeaders.Add('X-Internal-Api-Key', 'synthetic-local-internal-key')
    $Client.DefaultRequestHeaders.Add('X-Agent-Actor', 'demo-owner')
    $Client.DefaultRequestHeaders.Add('X-Agent-Role', 'Owner')
    $Client.DefaultRequestHeaders.Add('X-Agent-Timestamp', $Timestamp)
    $Client.DefaultRequestHeaders.Add('X-Agent-Signature', $Signature)
    $Form.Add([Net.Http.StringContent]::new('demo-owner'), 'profileId')
    $Audio = [Net.Http.ByteArrayContent]::new([Text.Encoding]::UTF8.GetBytes("Synthetic fixed transcript fixture $([Guid]::NewGuid())"))
    $Audio.Headers.ContentType = [Net.Http.Headers.MediaTypeHeaderValue]::new('audio/mp4')
    $Form.Add($Audio, 'file', 'synthetic-smoke.m4a')
    $Upload = $Client.PostAsync("$Api/api/coach-checkins/uploads", $Form).GetAwaiter().GetResult()
    Assert-Demo ($Upload.IsSuccessStatusCode) 'Upload accepted by the real API'
    $UploadId = ($Upload.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json).uploadId
} finally { $Form.Dispose(); $Client.Dispose() }
$Deadline = [DateTimeOffset]::UtcNow.AddSeconds(90)
do {
    $Status = (Invoke-DemoApi "/api/coach-checkins/$UploadId`?profileId=demo-owner").Content | ConvertFrom-Json
    if ($Status.status -in @('AwaitingSpeakerOverride', 'Failed')) { break }
    Start-Sleep -Seconds 1
} while ([DateTimeOffset]::UtcNow -lt $Deadline)
Assert-Demo ($Status.status -eq 'AwaitingSpeakerOverride') 'Worker consumes upload and persists the two-speaker review state'

# Real cookie login, CSRF rejection, and server-rendered role routing.
$NoToken = Invoke-WebRequest "$Web/demo/sign-in" -Method POST -Body @{ persona = 'owner' } -SkipHttpErrorCheck
Assert-Demo ($NoToken.StatusCode -eq 400) 'Synthetic login rejects missing antiforgery token'
foreach ($Persona in @('owner', 'coach', 'other')) {
    $Login = Invoke-WebRequest "$Web/login" -SessionVariable DemoSession
    $Token = [regex]::Match($Login.Content, 'name="__RequestVerificationToken" value="([^"]+)"').Groups[1].Value
    Assert-Demo (-not [string]::IsNullOrWhiteSpace($Token)) 'Login form issues an antiforgery token'
    $SignedIn = Invoke-WebRequest "$Web/demo/sign-in" -Method POST -WebSession $DemoSession -Body @{ persona = $Persona; __RequestVerificationToken = $Token }
    Assert-Demo ($SignedIn.StatusCode -eq 200 -and $DemoSession.Cookies.GetCookies([Uri]$Web)['SyntheticDemo.Auth']) "Sign in as $Persona with a real Web cookie"
    $Page = Invoke-WebRequest "$Web/coach-transcripts" -WebSession $DemoSession
    Assert-Demo ($Page.StatusCode -eq 200 -and -not $Page.Content.Contains('Choose a sample account')) "$Persona can load the authenticated transcript page"
}
Write-Host "Synthetic demo: $Checks checks passed."
