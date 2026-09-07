#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SnapshotPath,
    [Parameter(Mandatory)][ValidatePattern('^garden-restore-[a-z0-9][a-z0-9-]{0,45}$')][string]$ContainerName,
    [ValidateSet('Recovery','Development')][string]$Mode = 'Recovery',
    [ValidateRange(1024,65535)][int]$Port = 55432,
    [ValidatePattern('^[A-Za-z_][A-Za-z0-9_]*$')][string]$ConfigurationKeyEnvironmentVariable = 'SNAPSHOT_CONFIG_ENCRYPTION_KEY',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../snapshots/restores'),
    [string]$Image = 'pgvector/pgvector:0.8.2-pg18-trixie'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'lib/DatabaseSnapshot.psm1') -Force -DisableNameChecking

# Integrity checks precede any Docker/database mutation.
$snapshot = Read-SnapshotManifest $SnapshotPath
$endpoint = Assert-LocalSnapshotDocker
$existing = Invoke-SnapshotDocker -Arguments @('--host',$endpoint,'ps','--all','--format','{{.Names}}') -Operation 'Check restore target'
if ($ContainerName -in ($existing -split "`n")) { throw 'Restore target already exists; choose a new container name. Existing containers are never replaced.' }
$archiveDirectory = [IO.Path]::GetDirectoryName($snapshot.Archive)
$archiveName = [IO.Path]::GetFileName($snapshot.Archive)
$mount = Get-SnapshotMount $archiveDirectory '/snapshot' -ReadOnly
$reportDirectory = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) "$ContainerName-$([Guid]::NewGuid().ToString('N').Substring(0,8))"
[void][IO.Directory]::CreateDirectory($reportDirectory)
$started = [DateTimeOffset]::UtcNow
Initialize-SnapshotImage $endpoint $Image
$containerId = $null
$completed = $false
$random = New-Object byte[] 32
$rng = [Security.Cryptography.RandomNumberGenerator]::Create()
try { $rng.GetBytes($random) } finally { $rng.Dispose() }
$password = [Convert]::ToBase64String($random)
$priorPassword = [Environment]::GetEnvironmentVariable('POSTGRES_PASSWORD','Process')

try {
    [Environment]::SetEnvironmentVariable('POSTGRES_PASSWORD',$password,'Process')
    $arguments = @('--host',$endpoint,'create','--name',$ContainerName,'--label','digital-garden.snapshot-restore=true','--env','POSTGRES_PASSWORD','--env','POSTGRES_DB=garden','--mount',$mount)
    $arguments += if ($Mode -eq 'Recovery') { @('--network','none') } else { @('--publish',"127.0.0.1:${Port}:5432") }
    $containerId = Invoke-SnapshotDocker -Arguments ($arguments + @($Image)) -Operation 'Create fresh restore target'
    if ($containerId -notmatch '^[0-9a-f]{64}$') { throw 'Docker did not return a container ID.' }
    $null = Invoke-SnapshotDocker -Arguments @('--host',$endpoint,'start',$containerId) -Operation 'Start restore target'
    # The entrypoint initialization server accepts Unix sockets before restarting.
    # TCP readiness waits for the final server, which listens beyond that temporary socket.
    $ready = $false
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        try {
            $null = Invoke-SnapshotDocker -Arguments @('--host',$endpoint,'exec',$containerId,'pg_isready','--host','127.0.0.1','-U','postgres','-d','garden') -Operation 'Wait for restore target'
            $ready = $true
            break
        }
        catch { Start-Sleep -Seconds 1 }
    }
    if (-not $ready) { throw 'Restore target did not become ready within 60 seconds.' }
    $targetVersion = [int](Invoke-RestoreSql $endpoint $containerId "SELECT current_setting('server_version_num');")
    if ([Math]::Floor($targetVersion / 10000) -ne [Math]::Floor([int]$snapshot.Manifest.ServerVersionNumber / 10000)) { throw 'Restore requires the same PostgreSQL major version as the source; choose a compatible image.' }
    Write-Host "Restoring into new local container '$ContainerName' ($Mode)."
    $null = Invoke-SnapshotDocker -Arguments @('--host',$endpoint,'exec',$containerId,'pg_restore','--exit-on-error','--single-transaction','--no-owner','--no-privileges','--no-tablespaces','-U','postgres','--dbname','garden',"/snapshot/$archiveName") -Operation 'Restore archive'
    $targetExtensions = @(Invoke-RestoreSql $endpoint $containerId "SELECT coalesce(json_agg(json_build_object('Name',extname,'Version',extversion)), '[]'::json) FROM pg_extension;" | ConvertFrom-Json)
    foreach ($extension in $snapshot.Manifest.Extensions) {
        if (-not ($targetExtensions | Where-Object { $_.Name -eq $extension.Name -and $_.Version -eq $extension.Version })) { throw 'Restored extension versions differ from the source; use a compatible image.' }
    }
    $hasConfiguration = Invoke-RestoreSql $endpoint $containerId "SELECT to_regclass('app.configuration_settings') IS NOT NULL;"
    if ($hasConfiguration -ne 't') { throw 'Expected app.configuration_settings is missing; this is not a configured garden database.' }
    $settings = @(Invoke-RestoreSql $endpoint $containerId "SELECT coalesce(json_agg(row_to_json(c)), '[]'::json) FROM app.configuration_settings c;" | ConvertFrom-Json)
    $validatedSecrets = Assert-SnapshotConfigurationKey -Settings $settings -Key ([Environment]::GetEnvironmentVariable($ConfigurationKeyEnvironmentVariable))
    if ($Mode -eq 'Development') {
        # Fail closed for layouts the state-reset SQL does not cover.
        $defaults = @{
            'AgentMemory:Schema' = 'agent_memory'; 'Messaging:Schema' = 'transport'; 'CoachCheckins:Schema' = 'agent_memory'
            'AgentMemory:MobileDeviceTokensTableName' = 'mobile_device_tokens'; 'AgentMemory:AgentApprovalsTableName' = 'agent_approvals'
        }
        foreach ($setting in $settings) {
            if ($defaults.ContainsKey($setting.key) -and ($setting.is_secret -or $setting.value -ne $defaults[$setting.key])) { throw 'Development reset supports the default schema/table layout only; custom layouts require an explicit reset policy.' }
        }
        $resetSql = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'sql/reset-development-state.sql'))
        $null = Invoke-RestoreSql $endpoint $containerId $resetSql
    }
    $null = Invoke-RestoreSql $endpoint $containerId 'ANALYZE;'
    $report = [ordered]@{
        FormatVersion = 1; Mode = $Mode; ContainerName = $ContainerName; ContainerId = $containerId
        SnapshotSha256 = $snapshot.Manifest.Sha256; SnapshotStartedAtUtc = $snapshot.Manifest.StartedAtUtc
        StartedAtUtc = $started.ToString('O'); CompletedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        DurationSeconds = [Math]::Round(([DateTimeOffset]::UtcNow - $started).TotalSeconds,2)
        ValidatedEncryptedSettings = $validatedSecrets; Extensions = $targetExtensions
        Outcome = 'Database restored and encrypted configuration verified; application recovery not yet tested'
        ApplicationStarted = $false; PersonalDataRetained = $true
    }
    if ($Mode -eq 'Development') {
        $localUrl = "postgresql://postgres:$([Uri]::EscapeDataString($password))@localhost:$Port/garden"
        Write-SnapshotJson -Path (Join-Path $reportDirectory 'local-connection.json') -Value @{ DatabaseUrl = $localUrl; ConfigurationSeedRequired = $true }
    }
    Write-SnapshotJson -Path (Join-Path $reportDirectory 'report.json') -Value $report
    $completed = $true
    Write-Host "Restore verified. Evidence: $reportDirectory"
    if ($Mode -eq 'Development') { Write-Host 'Private application data retained. Supply a fresh local config seed before starting apps; do not reuse production credentials.' }
    else { Write-Host 'Recovery target has no network. Use docker exec for inspection; reconcile queued work before any application startup.' }
    [pscustomobject]@{ ContainerName = $ContainerName; ContainerId = $containerId; ReportDirectory = $reportDirectory; Mode = $Mode }
}
finally {
    [Environment]::SetEnvironmentVariable('POSTGRES_PASSWORD',$priorPassword,'Process')
    if (-not $completed -and $containerId -match '^[0-9a-f]{64}$') {
        # Only the exact container created by this invocation can be removed on failure.
        $null = Invoke-SnapshotDocker -Arguments @('--host',$endpoint,'rm','--force','--volumes',$containerId) -Operation 'Remove failed restore target'
    }
}
