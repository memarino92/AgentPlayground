param(
    [string]$EnvFilePath = ".env.railway"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. "$PSScriptRoot\Import-DotEnv.ps1"

function Get-UserSecretValue
{
    param(
        [string]$ProjectPath,
        [string]$Name
    )

    $dotnetCommand = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if ($null -eq $dotnetCommand)
    {
        return $null
    }

    if (-not (Test-Path -LiteralPath $ProjectPath))
    {
        return $null
    }

    $lines = & $dotnetCommand.Source user-secrets list --project $ProjectPath 2>$null
    if ($LASTEXITCODE -ne 0)
    {
        return $null
    }

    foreach ($line in $lines)
    {
        $separator = $line.IndexOf(" = ", [System.StringComparison]::Ordinal)
        if ($separator -lt 0) { continue }

        $key = $line.Substring(0, $separator).Trim()
        if ($key -ne $Name) { continue }

        return $line.Substring($separator + 3).Trim()
    }

    return $null
}

function Get-ResolvedSecretValue
{
    param(
        [string]$EnvironmentVariableName,
        [object[]]$UserSecretCandidates = @()
    )

    $environmentValue = [Environment]::GetEnvironmentVariable($EnvironmentVariableName)
    if (-not [string]::IsNullOrWhiteSpace($environmentValue))
    {
        return @{
            Value = $environmentValue
            Source = ".env.railway or process env"
        }
    }

    foreach ($candidate in $UserSecretCandidates)
    {
        $secretValue = Get-UserSecretValue -ProjectPath $candidate.ProjectPath -Name $candidate.SecretName
        if (-not [string]::IsNullOrWhiteSpace($secretValue))
        {
            return @{
                Value = $secretValue
                Source = $candidate.Source
            }
        }
    }

    return $null
}

$resolvedPath = Join-Path (Get-Location) $EnvFilePath
if (-not (Test-Path -LiteralPath $resolvedPath))
{
    throw "Env file '$EnvFilePath' was not found. Copy .env.railway.example to .env.railway first."
}

Import-DotEnv -Path $resolvedPath -Overwrite

$personalAgentProjectPath = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\PersonalAgent\PersonalAgent.csproj"))
$personalAgentWebProjectPath = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\PersonalAgent.Web\PersonalAgent.Web.csproj"))

$secretMappings = @(
    @{
        EnvironmentVariableName = "OPENAI_API_KEY"
        Description = "OpenAI API key for PersonalAgent API"
        Required = $true
        UserSecretCandidates = @(
            @{ ProjectPath = $personalAgentProjectPath; SecretName = "OpenApiKey"; Source = "PersonalAgent user secrets" }
        )
    },
    @{
        EnvironmentVariableName = "GITHUB_CLIENT_ID"
        Description = "GitHub OAuth app client ID"
        Required = $true
        UserSecretCandidates = @(
            @{ ProjectPath = $personalAgentWebProjectPath; SecretName = "Authentication:Schemes:GitHub:ClientId"; Source = "PersonalAgent.Web user secrets" }
        )
    },
    @{
        EnvironmentVariableName = "GITHUB_CLIENT_SECRET"
        Description = "GitHub OAuth app client secret"
        Required = $true
        UserSecretCandidates = @(
            @{ ProjectPath = $personalAgentWebProjectPath; SecretName = "Authentication:Schemes:GitHub:ClientSecret"; Source = "PersonalAgent.Web user secrets" }
        )
    },
    @{
        EnvironmentVariableName = "GITHUB_ALLOWED_USERS"
        Description = "Comma-separated GitHub usernames allowed to sign in"
        Required = $false
        UserSecretCandidates = @(
            @{ ProjectPath = $personalAgentWebProjectPath; SecretName = "Authentication:Schemes:GitHub:AllowedUsers"; Source = "PersonalAgent.Web user secrets" }
        )
    },
    @{
        EnvironmentVariableName = "GITHUB_CALLBACK_PATH"
        Description = "GitHub OAuth callback path override"
        Required = $false
        UserSecretCandidates = @(
            @{ ProjectPath = $personalAgentWebProjectPath; SecretName = "Authentication:Schemes:GitHub:CallbackPath"; Source = "PersonalAgent.Web user secrets" }
        )
    },
    @{
        EnvironmentVariableName = "INTERNAL_API_KEY"
        Description = "Shared internal API key between web and API services"
        Required = $false
        UserSecretCandidates = @(
            @{ ProjectPath = $personalAgentProjectPath; SecretName = "Security:InternalApiKey"; Source = "PersonalAgent user secrets" },
            @{ ProjectPath = $personalAgentProjectPath; SecretName = "InternalApiKey"; Source = "PersonalAgent user secrets" }
        )
    }
)

$resolvedSecretValues = @{}
foreach ($mapping in $secretMappings)
{
    $resolved = Get-ResolvedSecretValue -EnvironmentVariableName $mapping.EnvironmentVariableName -UserSecretCandidates $mapping.UserSecretCandidates
    if ($null -ne $resolved)
    {
        $resolvedSecretValues[$mapping.EnvironmentVariableName] = $resolved
    }
}

$requiredVariables = @(
    @{ Name = "RAILWAY_API_TOKEN"; Description = "Railway account or workspace token" },
    @{ Name = "RAILWAY_REPO_SLUG"; Description = "GitHub repo in owner/name format for repo-backed services" }
)

$recommendedVariables = @(
    @{ Name = "RAILWAY_PROJECT_NAME"; Description = "Railway project name for bootstrap" },
    @{ Name = "RAILWAY_REPO_BRANCH"; Description = "Git branch Railway should deploy" },
    @{ Name = "RAILWAY_ENVIRONMENT_NAME"; Description = "Railway environment name" },
    @{ Name = "RAILWAY_DATABASE_SERVICE_NAME"; Description = "Railway Postgres service name used in variable references" }
)

$missingRequired = @()

$missingRecommended = @()

foreach ($mapping in $secretMappings)
{
    if ($mapping.Required -and -not $resolvedSecretValues.ContainsKey($mapping.EnvironmentVariableName))
    {
        $missingRequired += @{ Name = $mapping.EnvironmentVariableName; Description = $mapping.Description }
    }

    if (-not $mapping.Required -and -not $resolvedSecretValues.ContainsKey($mapping.EnvironmentVariableName))
    {
        $missingRecommended += @{ Name = $mapping.EnvironmentVariableName; Description = $mapping.Description }
    }
}

foreach ($entry in $requiredVariables)
{
    $value = [Environment]::GetEnvironmentVariable($entry.Name)
    if ([string]::IsNullOrWhiteSpace($value))
    {
        $missingRequired += $entry
    }
}

foreach ($entry in $recommendedVariables)
{
    $value = [Environment]::GetEnvironmentVariable($entry.Name)
    if ([string]::IsNullOrWhiteSpace($value))
    {
        $missingRecommended += $entry
    }
}

Write-Host "Checked: $EnvFilePath"

if ($missingRequired.Count -eq 0)
{
    Write-Host "Required values: OK"
}
else
{
    Write-Host "Required values: missing"
    foreach ($entry in $missingRequired)
    {
        Write-Host "- $($entry.Name): $($entry.Description)"
    }
}

if ($resolvedSecretValues.Count -gt 0)
{
    Write-Host "Resolved secrets:"
    foreach ($mapping in $secretMappings)
    {
        if (-not $resolvedSecretValues.ContainsKey($mapping.EnvironmentVariableName))
        {
            continue
        }

        Write-Host "- $($mapping.EnvironmentVariableName): $($resolvedSecretValues[$mapping.EnvironmentVariableName].Source)"
    }
}

if ($missingRecommended.Count -eq 0)
{
    Write-Host "Recommended values: OK"
}
else
{
    Write-Host "Recommended values: review"
    foreach ($entry in $missingRecommended)
    {
        Write-Host "- $($entry.Name): $($entry.Description)"
    }
}

if ($missingRequired.Count -gt 0)
{
    throw "The env file is missing required values."
}
