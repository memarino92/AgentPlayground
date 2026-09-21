#requires -Version 7.0
[CmdletBinding()]
param()

# Runs only against the named local synthetic environment and its public fixture identities.
. "$PSScriptRoot/Test-SyntheticDemo.ps1"

$Owner = 'demo-other'
$Open = Invoke-DemoApi -Path '/api/conversation/open' -Actor $Owner -Method POST -Body @{ profileId = $Owner }
Assert-Demo ($Open.StatusCode -eq 200) 'Open the persistent default conversation'
$Conversation = $Open.Content | ConvertFrom-Json
$Id = $Conversation.sessionId
$Reopen = (Invoke-DemoApi -Path '/api/conversation/open' -Actor $Owner -Method POST -Body @{ profileId = $Owner }).Content | ConvertFrom-Json
Assert-Demo ($Reopen.sessionId -eq $Id) 'Opening again reuses the same conversation'

$Sent = Invoke-DemoApi -Path "/api/conversation/$Id/messages" -Actor $Owner -Method POST -Body @{ profileId = $Owner; message = 'Show demo cards' }
Assert-Demo ($Sent.StatusCode -eq 200) 'Continuous chat returns a saved response'
$Reply = ($Sent.Content | ConvertFrom-Json).messages[-1]
Assert-Demo ($Reply.presentation.cards.Count -eq 3 -and -not $Reply.content.Contains('```garden-card')) 'Three typed cards persist separately from readable text'
$Card = $Reply.presentation.cards | Where-Object kind -eq 'commitment'
$CardPath = "/api/conversation/$Id/cards/$($Reply.sequence)/$($Card.id)"
$Action = @{ profileId = $Owner; action = @{ revision = 0; action = 'confirm' } }
$Saved = Invoke-DemoApi -Path $CardPath -Actor $Owner -Method POST -Body $Action
Assert-Demo ($Saved.StatusCode -eq 200 -and ($Saved.Content | ConvertFrom-Json).status -eq 'active') 'Commitment confirmation is persisted'
Assert-Demo ((Invoke-DemoApi -Path $CardPath -Actor $Owner -Method POST -Body $Action).StatusCode -eq 409) 'Stale card revision is rejected'
$Denied = Invoke-DemoApi -Path $CardPath -Method POST -Body @{ profileId = 'demo-owner'; action = @{ revision = 1; action = 'complete' } }
Assert-Demo ($Denied.StatusCode -eq 403) 'A different signed-in owner cannot mutate the card'
$DeniedClear = Invoke-DemoApi -Path "/api/conversation/$Id/clear" -Method POST -Body @{ profileId = 'demo-owner' }
Assert-Demo ($DeniedClear.StatusCode -eq 404) 'A different signed-in owner cannot clear the conversation'
$Unsigned = Invoke-WebRequest "$Api/api/conversation/open" -Method POST -ContentType 'application/json' -Body '{"profileId":"demo-other"}' -Headers @{ 'X-Internal-Api-Key' = 'synthetic-local-internal-key' } -SkipHttpErrorCheck
Assert-Demo ($Unsigned.StatusCode -eq 401) 'Continuous chat requires a signed actor'

$Clear = Invoke-DemoApi -Path "/api/conversation/$Id/clear" -Actor $Owner -Method POST -Body @{ profileId = $Owner }
Assert-Demo ($Clear.StatusCode -eq 204) 'Fresh start saves a server-side context boundary'
$Fresh = (Invoke-DemoApi -Path '/api/conversation/open' -Actor $Owner -Method POST -Body @{ profileId = $Owner }).Content | ConvertFrom-Json
Assert-Demo ($Fresh.sessionId -eq $Id -and $Fresh.messages.Count -eq 0) 'Fresh start survives reopening without replacing the conversation'
$History = Invoke-DemoApi -Path "/api/conversation/history?profileId=$Owner&query=demo%20cards" -Actor $Owner
Assert-Demo ($History.StatusCode -eq 200 -and @($History.Content | ConvertFrom-Json).Count -gt 0) 'Explicit history search can still find cleared conversation records'
$Full = (Invoke-DemoApi -Path "/api/sessions/$Id/messages?profileId=$Owner" -Actor $Owner).Content | ConvertFrom-Json
Assert-Demo (($Full.messages | Where-Object { $_.sequence -eq $Reply.sequence }).presentation.cards[0].status -eq 'active') 'Clearing preserves confirmed cards in history'

Write-Host "Continuous conversation verification passed: $Checks checks including the base synthetic smoke suite."
