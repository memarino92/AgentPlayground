#requires -Version 7.0
[CmdletBinding()]
param([switch]$UnitOnly)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Import-Module (Join-Path $root 'scripts/lib/DatabaseSnapshot.psm1') -Force -DisableNameChecking
$script:checks = 0
function Assert-Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "Assertion failed: $Message" }
    $script:checks++
}
function Assert-Fails([scriptblock]$Action, [string]$Pattern) {
    try { $null = & $Action } catch {
        Assert-Check ($_.Exception.Message -match $Pattern) "Expected failure matching '$Pattern', got a different failure."
        return
    }
    throw "Expected failure matching '$Pattern'."
}

$parsed = ConvertFrom-SnapshotDatabaseUrl 'postgresql://user:p%40ss%3Aword@example.test:5544/garden?sslmode=require'
Assert-Check ($parsed.PGPASSWORD -eq 'p@ss:word' -and $parsed.PGPORT -eq '5544' -and $parsed.PGSSLMODE -eq 'require') 'URL decoding and SSL mode'
Assert-Fails { ConvertFrom-SnapshotDatabaseUrl 'Host=prod;Password=secret' } 'PostgreSQL URL'
Assert-Fails { ConvertFrom-SnapshotDatabaseUrl 'postgresql://user:pass@example.test/garden?host=elsewhere' } 'Only sslmode'
Assert-Fails { ConvertFrom-SnapshotDatabaseUrl 'postgresql://user:pass@example.test/garden#fragment' } 'fragment'
Assert-Fails { Get-SnapshotMount '/tmp/path,with-comma' '/snapshot' } 'commas'
$previousHost = [Environment]::GetEnvironmentVariable('DOCKER_HOST','Process')
try {
    [Environment]::SetEnvironmentVariable('DOCKER_HOST','tcp://remote.example.test:2375','Process')
    Assert-Fails { Assert-LocalSnapshotDocker } 'local Docker daemon'
}
finally { [Environment]::SetEnvironmentVariable('DOCKER_HOST',$previousHost,'Process') }
Assert-Fails { Read-SnapshotManifest (Join-Path $root 'does-not-exist.dump') } 'does not exist|Cannot find path'
if ($UnitOnly) { Write-Host "Passed $script:checks snapshot guard checks."; return }

$endpoint = Assert-LocalSnapshotDocker
$image = 'pgvector/pgvector:0.8.2-pg18-trixie'
Initialize-SnapshotImage $endpoint $image
$runId = [Guid]::NewGuid().ToString('N').Substring(0,10)
$testDirectory = Join-Path $root ".artifacts/snapshot-tests/$runId"
[void][IO.Directory]::CreateDirectory($testDirectory)
$created = [Collections.Generic.List[string]]::new()
$previous = @{}
foreach ($key in @('POSTGRES_PASSWORD','SNAPSHOT_DATABASE_URL','SNAPSHOT_CONFIG_ENCRYPTION_KEY')) { $previous[$key] = [Environment]::GetEnvironmentVariable($key,'Process') }
try {
    $env:POSTGRES_PASSWORD = 'snapshot-test-password'
    $sourceName = "garden-restore-test-source-$runId"
    $source = Invoke-SnapshotDocker -Arguments @('--host',$endpoint,'create','--name',$sourceName,'--env','POSTGRES_PASSWORD','--env','POSTGRES_DB=garden','--publish','127.0.0.1::5432',$image) -Operation 'Create synthetic source'
    $created.Add($source)
    $null = Invoke-SnapshotDocker -Arguments @('--host',$endpoint,'start',$source) -Operation 'Start synthetic source'
    $ready = $false
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        try { $null = Invoke-RestoreSql $endpoint $source 'SELECT 1;'; $ready = $true; break }
        catch { Start-Sleep -Seconds 1 }
    }
    Assert-Check $ready 'Synthetic source ready'
    $sourceInfo = @(Invoke-SnapshotDocker -Arguments @('--host',$endpoint,'inspect',$source) | ConvertFrom-Json)[0]
    $port = $sourceInfo.NetworkSettings.Ports.'5432/tcp'[0].HostPort
    $exportNetwork = if ($IsLinux) { 'host' } else { 'bridge' }
    $sourceHost = if ($IsLinux) { 'localhost' } else { 'host.docker.internal' }
    $env:SNAPSHOT_DATABASE_URL = "postgresql://postgres:snapshot-test-password@${sourceHost}:$port/garden?sslmode=disable"
    $configKey = [Convert]::ToBase64String([byte[]](1..32))
    $env:SNAPSHOT_CONFIG_ENCRYPTION_KEY = $configKey
    # Use the real seed implementation so restore key verification cannot pass on its own invented format.
    $values = [IO.File]::ReadAllText((Join-Path $root 'scripts/seed-configuration.values.ps1.example'))
    $values = [regex]::Replace($values,"'<[^']*>'","'snapshot-test-value'")
    $values = $values.Replace("`$ConfigEncryptionKey = 'snapshot-test-value'", "`$ConfigEncryptionKey = '$configKey'")
    $valuesPath = Join-Path $testDirectory 'values.ps1'
    [IO.File]::WriteAllText($valuesPath,$values)
    $seedPath = Join-Path $testDirectory 'seed.sql'
    & (Join-Path $root 'scripts/seed-configuration.ps1') -ValuesPath $valuesPath -OutputPath $seedPath
    $null = Invoke-RestoreSql $endpoint $source ([IO.File]::ReadAllText($seedPath))
    $null = Invoke-RestoreSql $endpoint $source ([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'snapshot-fixture.sql')))
    $export = & (Join-Path $root 'scripts/export-database-snapshot.ps1') -SourceName fixture -OutputDirectory $testDirectory -Network $exportNetwork
    $manifestText = [IO.File]::ReadAllText($export.ManifestPath)
    Assert-Check (-not $manifestText.Contains('snapshot-test-password') -and -not $manifestText.Contains('host.docker.internal') -and -not $manifestText.Contains($configKey)) 'Manifest does not disclose source credentials'
    $parameters = @{ SnapshotPath = $export.ArchivePath; OutputDirectory = $testDirectory }
    $restoreScript = Join-Path $root 'scripts/restore-database-snapshot.ps1'
    Assert-Fails { & $restoreScript @parameters -ContainerName $sourceName } 'already exists'
    Assert-Check ((Invoke-RestoreSql $endpoint $source 'SELECT count(*) FROM transport.pending_work;') -eq '1') 'Existing target unchanged'

    $recovery = & $restoreScript @parameters -ContainerName "garden-restore-test-recovery-$runId"
    $created.Add($recovery.ContainerId)
    Assert-Check ((Invoke-RestoreSql $endpoint $recovery.ContainerId 'SELECT count(*) FROM transport.pending_work;') -eq '1') 'Recovery preserves pending work'
    Assert-Check ((Invoke-RestoreSql $endpoint $recovery.ContainerId 'SELECT count(*) FROM agent_memory.mobile_device_tokens;') -eq '1') 'Recovery preserves device records'
    Assert-Check ((Invoke-RestoreSql $endpoint $recovery.ContainerId "SELECT content FROM agent_memory.memory_records ORDER BY embedding <=> '[1,0,0]' LIMIT 1;") -eq 'Synthetic garden note') 'Vector retrieval survives restore'
    $recoveryInfo = @(Invoke-SnapshotDocker -Arguments @('--host',$endpoint,'inspect',$recovery.ContainerId) | ConvertFrom-Json)[0]
    Assert-Check ($recoveryInfo.HostConfig.NetworkMode -eq 'none') 'Recovery target is isolated'
    $report = Get-Content -Raw (Join-Path $recovery.ReportDirectory 'report.json') | ConvertFrom-Json
    Assert-Check ($report.ValidatedEncryptedSettings -gt 0 -and -not $report.ApplicationStarted) 'Real encrypted settings verified; evidence does not claim app recovery'

    # Select a currently free loopback port; Docker still fails safely if another process wins the race.
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
    $listener.Start(); $developmentPort = $listener.LocalEndpoint.Port; $listener.Stop()
    $development = & $restoreScript @parameters -ContainerName "garden-restore-test-dev-$runId" -Mode Development -Port $developmentPort
    $created.Add($development.ContainerId)
    $developmentInfo = @(Invoke-SnapshotDocker -Arguments @('--host',$endpoint,'inspect',$development.ContainerId) | ConvertFrom-Json)[0]
    Assert-Check ($developmentInfo.HostConfig.PortBindings.'5432/tcp'[0].HostIp -eq '127.0.0.1') 'Development port binds only loopback'
    Assert-Check ((Invoke-RestoreSql $endpoint $development.ContainerId "SELECT to_regnamespace('transport') IS NULL;") -eq 't') 'Development clears queued work'
    foreach ($table in @('app.data_protection_keys','agent_memory.mobile_device_tokens','agent_memory.agent_approvals')) {
        Assert-Check ((Invoke-RestoreSql $endpoint $development.ContainerId "SELECT count(*) FROM $table;") -eq '0') "Development resets $table"
    }
    Assert-Check ((Invoke-RestoreSql $endpoint $development.ContainerId 'SELECT count(*) FROM app.configuration_settings WHERE is_secret;') -eq '0') 'Development clears provider credentials'
    Assert-Check ((Invoke-RestoreSql $endpoint $development.ContainerId 'SELECT count(*) FROM agent_memory.tool_role_permissions WHERE is_enabled;') -eq '0') 'Development disables restored action permissions'
    Assert-Check ((Invoke-RestoreSql $endpoint $development.ContainerId 'SELECT count(*) FROM agent_memory.transcript_messages;') -eq '1') 'Development retains domain data'
    Assert-Check ((Invoke-RestoreSql $endpoint $development.ContainerId 'SELECT count(*) FROM agent_memory.coach_profile_assignments;') -eq '1') 'Development retains assignments'

    $failureName = "garden-restore-test-failed-$runId"
    $env:SNAPSHOT_CONFIG_ENCRYPTION_KEY = ''
    Assert-Fails { & $restoreScript @parameters -ContainerName $failureName } 'configuration key'
    $env:SNAPSHOT_CONFIG_ENCRYPTION_KEY = [Convert]::ToBase64String([byte[]](33..64))
    Assert-Fails { & $restoreScript @parameters -ContainerName $failureName } 'decryption failed'
    $env:SNAPSHOT_CONFIG_ENCRYPTION_KEY = $configKey
    $names = Invoke-SnapshotDocker -Arguments @('--host',$endpoint,'ps','--all','--format','{{.Names}}')
    Assert-Check ($failureName -notin ($names -split "`n")) 'Failed restores remove only their newly created container'

    $corrupt = Join-Path $testDirectory 'corrupt.dump'
    [IO.File]::WriteAllText($corrupt,'not a PostgreSQL archive')
    Assert-Fails { Read-SnapshotManifest $corrupt } 'manifest is missing'
    Copy-Item -LiteralPath $export.ManifestPath -Destination "$corrupt.json"
    Assert-Fails { Read-SnapshotManifest $corrupt } 'size or checksum'
    $badManifest = Get-Content -Raw $export.ManifestPath | ConvertFrom-Json
    $badManifest.SizeBytes = (Get-Item $corrupt).Length
    Write-SnapshotJson "$corrupt.json" $badManifest
    Assert-Fails { Read-SnapshotManifest $corrupt } 'checksum mismatch'
    $badManifest.Sha256 = (Get-FileHash $corrupt -Algorithm SHA256).Hash
    Write-SnapshotJson "$corrupt.json" $badManifest
    Assert-Fails { & $restoreScript -SnapshotPath $corrupt -ContainerName $failureName -OutputDirectory $testDirectory } 'Restore archive failed'
    $names = Invoke-SnapshotDocker -Arguments @('--host',$endpoint,'ps','--all','--format','{{.Names}}')
    Assert-Check ($failureName -notin ($names -split "`n")) 'Corrupt archive failure cleans target'
    # A custom configured layout must not be falsely labelled reset for development.
    $null = Invoke-RestoreSql $endpoint $source "INSERT INTO app.configuration_settings VALUES ('Api','AgentMemory:Schema','custom_memory',false,true,now());"
    $customExport = & (Join-Path $root 'scripts/export-database-snapshot.ps1') -SourceName custom -OutputDirectory $testDirectory -Network $exportNetwork
    Assert-Fails { & $restoreScript -SnapshotPath $customExport.ArchivePath -ContainerName $failureName -Mode Development -Port $developmentPort -OutputDirectory $testDirectory } 'Start restore target failed|default schema/table layout'
    # Retry on an available port to specifically exercise the layout guard, not a port conflict.
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
    $listener.Start(); $customPort = $listener.LocalEndpoint.Port; $listener.Stop()
    Assert-Fails { & $restoreScript -SnapshotPath $customExport.ArchivePath -ContainerName $failureName -Mode Development -Port $customPort -OutputDirectory $testDirectory } 'default schema/table layout'
    $names = Invoke-SnapshotDocker -Arguments @('--host',$endpoint,'ps','--all','--format','{{.Names}}')
    Assert-Check ($failureName -notin ($names -split "`n")) 'Startup/layout failures clean their targets'
    Assert-Check ((Get-FileHash $export.ArchivePath -Algorithm SHA256).Hash -eq $export.Sha256) 'Original snapshot remains immutable'
    Write-Host "Passed $script:checks snapshot checks. Synthetic evidence: $testDirectory"
}
finally {
    foreach ($id in $created) {
        if ($id -notmatch '^[0-9a-f]{64}$') { throw 'Unexpected test container ID; refusing cleanup.' }
        $null = Invoke-SnapshotDocker -Arguments @('--host',$endpoint,'rm','--force','--volumes',$id) -Operation 'Remove synthetic test container'
    }
    foreach ($key in $previous.Keys) { [Environment]::SetEnvironmentVariable($key,$previous[$key],'Process') }
}
