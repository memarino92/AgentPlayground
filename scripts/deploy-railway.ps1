param(
    [string]$ProjectId,
    [string]$EnvironmentName = "production",
    [string]$EnvFilePath = ".env.railway"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. "$PSScriptRoot\Import-DotEnv.ps1"
Import-DotEnv -Path (Join-Path (Get-Location) $EnvFilePath)

if ([string]::IsNullOrWhiteSpace($ProjectId))
{
    $ProjectId = [Environment]::GetEnvironmentVariable("RAILWAY_PROJECT_ID")
}

if ($PSBoundParameters.ContainsKey("EnvironmentName") -eq $false)
{
    $EnvironmentName = [Environment]::GetEnvironmentVariable("RAILWAY_ENVIRONMENT_NAME") ?? $EnvironmentName
}

if ([string]::IsNullOrWhiteSpace($ProjectId))
{
    throw "ProjectId is required. Pass -ProjectId or set RAILWAY_PROJECT_ID in the env file."
}

$railwayCommand = Get-Command railway.exe -ErrorAction SilentlyContinue
if ($null -eq $railwayCommand)
{
    throw "Railway CLI is required. Install it and ensure 'railway.exe' is on PATH."
}

function Write-Status
{
    param([string]$Message)

    Write-Host $Message
    [Console]::Out.Flush()
    [Console]::Error.Flush()
}

& $railwayCommand.Source link --project-id $ProjectId --environment $EnvironmentName | Out-Null
if ($LASTEXITCODE -ne 0)
{
    throw "Failed to link Railway CLI to project '$ProjectId'."
}

foreach ($service in @("personalagent-api", "personalagent-web", "personalagent-worker"))
{
    Write-Status "Deploying $service..."
    $commandOutput = & $railwayCommand.Source up --service $service --detach --json 2>&1

    if ($LASTEXITCODE -ne 0)
    {
        if ($commandOutput)
        {
            $commandOutput | Out-Host
        }

        throw "Deployment failed for service '$service'."
    }

    $queuedDeploymentId = $null
    if ($commandOutput)
    {
        try
        {
            $jsonOutput = ($commandOutput | Out-String).Trim()
            if (-not [string]::IsNullOrWhiteSpace($jsonOutput))
            {
                $parsed = $jsonOutput | ConvertFrom-Json
                $queuedDeploymentId = $parsed.id
            }
        }
        catch
        {
            $commandOutput | Out-Host
        }
    }

    if ([string]::IsNullOrWhiteSpace($queuedDeploymentId))
    {
        Write-Status "Queued deploy for $service."
    }
    else
    {
        Write-Status "Queued deploy for ${service}: $queuedDeploymentId"
    }
}

Write-Status "Deployments queued for all services. Use 'railway logs --service <name>' to inspect progress."
