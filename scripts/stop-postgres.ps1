param(
    [string]$ContainerName = "agentplayground-postgres"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$dockerCommand = Get-Command docker.exe -ErrorAction SilentlyContinue
if ($null -eq $dockerCommand)
{
    throw "Docker Desktop is required on Windows. Install Docker Desktop and ensure 'docker.exe' is on PATH."
}

$existing = & $dockerCommand.Source ps -a --filter "name=^/${ContainerName}$" --format "{{.Names}}"

if ($LASTEXITCODE -ne 0)
{
    throw "Docker Desktop does not appear to be running. Start Docker Desktop and try again."
}

if ($existing -ne $ContainerName)
{
    Write-Host "PostgreSQL container '$ContainerName' does not exist."
    exit 0
}

& $dockerCommand.Source stop $ContainerName | Out-Null

if ($LASTEXITCODE -ne 0)
{
    throw "Failed to stop PostgreSQL container '$ContainerName'."
}

Write-Host "PostgreSQL container '$ContainerName' stopped."
