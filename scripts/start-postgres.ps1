param(
    [string]$ContainerName = "agentplayground-postgres",
    [string]$Database = "agentplayground",
    [string]$Username = "agentplayground",
    [string]$Password = "agentplayground",
    [int]$Port = 5432,
    [string]$Image = "pgvector/pgvector:0.8.2-pg18-trixie"
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

if ($existing -eq $ContainerName)
{
    & $dockerCommand.Source start $ContainerName | Out-Null

    if ($LASTEXITCODE -ne 0)
    {
        throw "Failed to start PostgreSQL container '$ContainerName'."
    }
}
else
{
    & $dockerCommand.Source run -d --name $ContainerName -e POSTGRES_DB=$Database -e POSTGRES_USER=$Username -e POSTGRES_PASSWORD=$Password -p "${Port}:5432" $Image | Out-Null

    if ($LASTEXITCODE -ne 0)
    {
        throw "Failed to create PostgreSQL container '$ContainerName'."
    }
}

Write-Host "PostgreSQL container '$ContainerName' is running on port $Port."
Write-Host "Image: $Image"
Write-Host "Connection string: Host=localhost;Port=$Port;Database=$Database;Username=$Username;Password=$Password"
