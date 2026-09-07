#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9_-]{0,63}$')][string]$SourceName,
    [ValidatePattern('^[A-Za-z_][A-Za-z0-9_]*$')][string]$ConnectionEnvironmentVariable = 'SNAPSHOT_DATABASE_URL',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../snapshots'),
    [string]$Image = 'pgvector/pgvector:0.8.2-pg18-trixie',
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.:-]{0,127}$')][string]$Network = 'bridge'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'lib/DatabaseSnapshot.psm1') -Force -DisableNameChecking

$connection = ConvertFrom-SnapshotDatabaseUrl ([Environment]::GetEnvironmentVariable($ConnectionEnvironmentVariable))
$endpoint = Assert-LocalSnapshotDocker
Initialize-SnapshotImage $endpoint $Image
$directory = [IO.Path]::GetFullPath($OutputDirectory)
[void][IO.Directory]::CreateDirectory($directory)
$mount = Get-SnapshotMount $directory '/snapshot'
$id = "$SourceName-$([DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ'))-$([Guid]::NewGuid().ToString('N').Substring(0,8))"
$archive = Join-Path $directory "$id.dump"
$partial = "$archive.partial"
$started = [DateTimeOffset]::UtcNow

$metadataSql = "SELECT json_build_object('ServerVersion',current_setting('server_version'),'ServerVersionNumber',current_setting('server_version_num')::int,'Extensions',(SELECT coalesce(json_agg(json_build_object('Name',extname,'Version',extversion)), '[]'::json) FROM pg_extension));"
$metadata = Invoke-SnapshotClient -Endpoint $endpoint -Image $Image -Network $Network -Connection $connection -Command @('psql','-X','--no-password','--tuples-only','--no-align','--set','ON_ERROR_STOP=1','--command',$metadataSql) | ConvertFrom-Json
$clientVersion = Invoke-SnapshotClient -Endpoint $endpoint -Image $Image -Network $Network -Connection @{} -Command @('pg_dump','--version')
try {
    Write-Host "Exporting full snapshot for '$SourceName'."
    $null = Invoke-SnapshotClient -Endpoint $endpoint -Image $Image -Network $Network -Connection $connection -Mounts @($mount) -Command @('pg_dump','--no-password','--format=custom','--compress=zstd:9','--file',"/snapshot/$id.dump.partial")
    $null = Invoke-SnapshotClient -Endpoint $endpoint -Image $Image -Network $Network -Connection @{} -Mounts @($mount) -Command @('pg_restore','--list',"/snapshot/$id.dump.partial")
    $commit = & git -C (Join-Path $PSScriptRoot '..') rev-parse HEAD 2>$null
    if ($LASTEXITCODE -ne 0) { $commit = $null }
    $manifest = [ordered]@{
        FormatVersion = 1; Format = 'PostgreSQLCustom'; Mode = 'Full'; SourceName = $SourceName
        StartedAtUtc = $started.ToString('O'); CompletedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        ServerVersion = $metadata.ServerVersion; ServerVersionNumber = $metadata.ServerVersionNumber
        ClientVersion = $clientVersion; Extensions = $metadata.Extensions; ExporterCommit = $commit
        SizeBytes = (Get-Item -LiteralPath $partial).Length
        Sha256 = (Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash
    }
    # Publish the manifest last: incomplete exports are never accepted by restore.
    [IO.File]::Move($partial,$archive)
    Write-SnapshotJson -Path "$archive.json" -Value $manifest
    Write-Host "Snapshot complete: $archive"
    [pscustomobject]@{ ArchivePath = $archive; ManifestPath = "$archive.json"; Sha256 = $manifest.Sha256 }
}
finally {
    if ([IO.File]::Exists($partial)) { [IO.File]::Delete($partial) }
}
