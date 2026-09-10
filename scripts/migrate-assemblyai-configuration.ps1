[CmdletBinding()]
param(
    [string]$ValuesPath,
    [switch]$Apply,
    [switch]$KeepSource
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$previousDatabaseUrl = [Environment]::GetEnvironmentVariable('DATABASE_URL', 'Process')
$previousEncryptionKey = [Environment]::GetEnvironmentVariable('CONFIG_ENCRYPTION_KEY', 'Process')
try
{
    if ($ValuesPath)
    {
        $resolvedValuesPath = (Resolve-Path -LiteralPath $ValuesPath).Path
        # Use only the bootstrap values from the existing private seed file.
        $DatabaseUrl = $null
        $ConfigEncryptionKey = $null
        . $resolvedValuesPath
        if ($DatabaseUrl) { [Environment]::SetEnvironmentVariable('DATABASE_URL', $DatabaseUrl, 'Process') }
        if ($ConfigEncryptionKey) { [Environment]::SetEnvironmentVariable('CONFIG_ENCRYPTION_KEY', $ConfigEncryptionKey, 'Process') }
    }
    $migrationProject = Join-Path $PSScriptRoot 'ConfigurationScopeMigration/ConfigurationScopeMigration.csproj'
    $migrationArguments = @('run', '--project', $migrationProject, '--no-launch-profile', '--')
    if ($Apply) { $migrationArguments += '--apply' }
    if ($KeepSource) { $migrationArguments += '--keep-source' }
    & dotnet @migrationArguments
    if ($LASTEXITCODE -ne 0) { throw 'AssemblyAI configuration migration did not complete.' }
}
finally
{
    [Environment]::SetEnvironmentVariable('DATABASE_URL', $previousDatabaseUrl, 'Process')
    [Environment]::SetEnvironmentVariable('CONFIG_ENCRYPTION_KEY', $previousEncryptionKey, 'Process')
}
