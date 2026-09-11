#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
# Reuse the synthetic-only signed API helper and validate the existing stack first.
. "$PSScriptRoot/Test-SyntheticDemo.ps1"

# Valid 30-second PCM silence, with a unique final sample to avoid upload deduplication.
$Stream = [IO.MemoryStream]::new()
$Writer = [IO.BinaryWriter]::new($Stream)
$Data = [byte[]]::new(480000)
[Guid]::NewGuid().ToByteArray().CopyTo($Data, $Data.Length - 16)
$Writer.Write([Text.Encoding]::ASCII.GetBytes('RIFF'))
$Writer.Write([int](36 + $Data.Length))
$Writer.Write([Text.Encoding]::ASCII.GetBytes('WAVEfmt '))
$Writer.Write([int]16)
$Writer.Write([short]1)
$Writer.Write([short]1)
$Writer.Write([int]8000)
$Writer.Write([int]16000)
$Writer.Write([short]2)
$Writer.Write([short]16)
$Writer.Write([Text.Encoding]::ASCII.GetBytes('data'))
$Writer.Write([int]$Data.Length)
$Writer.Write($Data)
$Bytes = $Stream.ToArray()
$Writer.Dispose()
$Stream.Dispose()

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
    $Audio = [Net.Http.ByteArrayContent]::new($Bytes)
    $Audio.Headers.ContentType = [Net.Http.Headers.MediaTypeHeaderValue]::new('audio/wav')
    # The existing upload route requires an .m4a suffix; the synthetic provider ignores
    # the filename. Preserve audio/wav and valid PCM bytes for browser playback testing.
    $Form.Add($Audio, 'file', 'synthetic-evidence-silence.m4a')
    $Upload = $Client.PostAsync("$Api/api/coach-checkins/uploads", $Form).GetAwaiter().GetResult()
    Assert-Demo ($Upload.IsSuccessStatusCode) 'Valid synthetic WAV uploaded'
    $EvidenceUploadId = ($Upload.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json).uploadId
} finally { $Form.Dispose(); $Client.Dispose() }

function Wait-EvidenceStatus([string] $Expected) {
    $Deadline = [DateTimeOffset]::UtcNow.AddSeconds(60)
    do {
        $State = (Invoke-DemoApi "/api/coach-checkins/$EvidenceUploadId`?profileId=demo-owner").Content | ConvertFrom-Json
        if ($State.status -eq $Expected) { return }
        if ($State.status -eq 'Failed') { throw 'Synthetic evidence processing failed' }
        Start-Sleep -Milliseconds 250
    } while ([DateTimeOffset]::UtcNow -lt $Deadline)
    throw "Timed out awaiting $Expected"
}
Wait-EvidenceStatus 'AwaitingSpeakerOverride'
$Override = Invoke-DemoApi -Path "/api/coach-checkins/$EvidenceUploadId/speaker-overrides" -Method POST -Body @{
    profileId = 'demo-owner'; overrides = @(@{speakerLabel = 0; role = 'athlete'}, @{speakerLabel = 1; role = 'coach'})
}
Assert-Demo ($Override.StatusCode -eq 202 -or $Override.StatusCode -eq 200) 'Speaker review continued'
Wait-EvidenceStatus 'Completed'
$Evidence = (Invoke-DemoApi "/api/coach-checkins/$EvidenceUploadId/evidence?profileId=demo-owner").Content | ConvertFrom-Json
Assert-Demo ($Evidence.audioAvailable -and $Evidence.transcript.utterances[1].startMs -eq 4000) 'Completed evidence retains recording and source timeline'

foreach ($Persona in @('owner', 'coach', 'other')) {
    $Login = Invoke-WebRequest "$Web/login" -SessionVariable EvidenceSession
    $Token = [regex]::Match($Login.Content, 'name="__RequestVerificationToken" value="([^"]+)"').Groups[1].Value
    $null = Invoke-WebRequest "$Web/demo/sign-in" -Method POST -WebSession $EvidenceSession -Body @{ persona = $Persona; __RequestVerificationToken = $Token }
    $Range = Invoke-WebRequest "$Web/media/coach-checkins/$EvidenceUploadId/audio?profileId=demo-owner" -WebSession $EvidenceSession -Headers @{Range='bytes=0-3'} -SkipHttpErrorCheck
    Assert-Demo ($Range.StatusCode -eq $(if ($Persona -eq 'other') {403} else {206})) "$Persona media range enforces API subject policy through Web cookie"
    if ($Persona -ne 'other') {
        Assert-Demo ([Text.Encoding]::ASCII.GetString($Range.Content) -eq 'RIFF') 'Web proxy returns exact original audio range'
    }
}
Write-Host "Retained audio evidence: $Checks checks passed."
Write-Host "Browser fixture: $Web/evidence/$EvidenceUploadId`?profileId=demo-owner&startMs=4000"
