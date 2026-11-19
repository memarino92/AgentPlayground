# Creates and destroys its own isolated VM. Only synthetic data is used.
param([Parameter(Mandatory)][string] $EnvironmentName, [switch] $PrepareCheckpoint, [string] $Checkpoint)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent

function Invoke-ScratchCommand([string] $Command, [int] $Timeout = 45) {
    $result = & railway sandbox exec --id $SandboxId --timeout $Timeout -- $Command
    if ($LASTEXITCODE -ne 0) { throw "Railway scratchpad check failed (exit $LASTEXITCODE)." }
    return ($result -join [Environment]::NewLine).Trim()
}

function Invoke-Cell([string] $Code) {
    $payload = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Code))
    Invoke-ScratchCommand "echo '$payload' | base64 -d | docker exec -i scratch python3 /template/scratchpad.py submit"
}

if ($PrepareCheckpoint -and $Checkpoint) { throw 'Choose a new build or an existing checkpoint.' }
$createArgs = @('sandbox','create','-e',$EnvironmentName,'--idle-timeout-minutes','10','--json')
if ($Checkpoint) { $createArgs += @('--checkpoint',$Checkpoint) }
$created = & railway @createArgs | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or !$created.id) { throw 'Could not create the Railway proof sandbox.' }
$SandboxId = [guid]$created.id
Write-Output "Railway proof VM $SandboxId"
try {
    $ready = $false
    for ($attempt = 0; $attempt -lt 25; $attempt++) {
        & railway sandbox exec --id $SandboxId --timeout 5 -- true 2>$null
        if ($LASTEXITCODE -eq 0) { $ready = $true; break }
        Start-Sleep -Seconds 2
    }
    if (!$ready) { throw 'Railway VM never became ready.' }
    if (!$Checkpoint) {
        $files = @('Dockerfile.automation-sandbox','automation-sandbox/scratchpad.py','automation-sandbox/run.sh','automation-sandbox/Program.csproj','automation-sandbox/NuGet.Config')
        $setup = @('set -eu', 'mkdir -p /proof/automation-sandbox', 'cd /proof')
        foreach ($file in $files) {
            $payload = [Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $projectRoot $file)))
            $setup += "echo '$payload' | base64 -d > '$file'"
        }
        $setup += 'docker build -q -f Dockerfile.automation-sandbox -t scratch-proof .'
        $image = Invoke-ScratchCommand ($setup -join [char]10) 360
        if ($image -notmatch '^sha256:[0-9a-f]{64}$') { throw 'Unexpected runtime image identity.' }
        if ($PrepareCheckpoint) {
            $checkpoint = "scratchpad-$($SandboxId.ToString('N'))"
            & railway sandbox checkpoint create $checkpoint --id $SandboxId --json
            if ($LASTEXITCODE -ne 0) { throw 'Checkpoint capture failed.' }
            Write-Output "Prepared checkpoint $checkpoint with image $image"
        }
    }
    Invoke-ScratchCommand 'docker volume create automation-artifacts && docker run --rm --network=none --read-only --cap-drop=ALL --cap-add=CHOWN --user=0 -v automation-artifacts:/artifacts --entrypoint /bin/chown scratch-proof 65532:65532 /artifacts'
    $start = [Diagnostics.Stopwatch]::StartNew()
    Invoke-ScratchCommand 'docker run -d --name scratch --read-only --user=65532:65532 --cap-drop=ALL --security-opt=no-new-privileges --memory=512m --cpus=1 --pids-limit=128 --tmpfs /tmp:rw,size=64m,mode=1777 --tmpfs /work:rw,size=256m,mode=1777 -v automation-artifacts:/artifacts --entrypoint python3 scratch-proof /template/scratchpad.py serve'
    if ((Invoke-Cell "import pandas as pd; df=pd.DataFrame({'x':[1,3,5]}); int(df.x.sum())") -ne '9') { throw 'First cell failed' }
    Write-Output "First cell including container start: $($start.ElapsedMilliseconds) ms"
    $timer = [Diagnostics.Stopwatch]::StartNew()
    Invoke-Cell "import matplotlib.pyplot as plt; df.plot(); plt.title('Synthetic scratchpad result')"
    for ($attempt=0; $attempt -lt 3; $attempt++) {
        try { $magic = Invoke-ScratchCommand 'docker exec scratch od -An -tx1 -N8 /artifacts/plot.png'; break }
        catch { if ($attempt -eq 2) { throw } }
    }
    if (($magic -replace '\s','') -ne '89504e470d0a1a0a') { throw 'Invalid PNG signature.' }
    Write-Output "Chart creation and PNG verification: $($timer.ElapsedMilliseconds) ms"
    $failures = 0
    $times = foreach ($iteration in 1..20) {
        $timer = [Diagnostics.Stopwatch]::StartNew()
        try { $sum = Invoke-Cell 'int(df.x.sum())' }
        catch { $failures++; continue }
        if ($sum -ne '9') { throw 'Warm cell lost its DataFrame' }
        $timer.ElapsedMilliseconds
    }
    if ($times.Count -eq 0) { throw 'No warm sample completed.' }
    $sorted = @($times | Sort-Object)
    $p95 = $sorted[[Math]::Ceiling($sorted.Count * .95)-1]
    Write-Output "Warm CLI p95: $p95 ms; $($sorted.Count) successful, $failures failed."
} finally {
    & railway sandbox destroy --id $SandboxId
    if ($LASTEXITCODE -ne 0) { throw "Verify cleanup of Railway test VM $SandboxId" }
}
