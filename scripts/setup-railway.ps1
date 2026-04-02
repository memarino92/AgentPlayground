param(
    [string]$ProjectName = "AgentPlayground",
    [string]$ProjectId,
    [string]$EnvironmentName = "production",
    [string]$RepoSlug,
    [string]$Branch = "main",
    [string]$WorkspaceId,
    [string]$DatabaseServiceName = "Postgres",
    [string]$EnvFilePath = ".env.railway",
    [switch]$CreatePostgres
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. "$PSScriptRoot\Import-DotEnv.ps1"
Import-DotEnv -Path (Join-Path (Get-Location) $EnvFilePath)

if ($PSBoundParameters.ContainsKey("ProjectName") -eq $false)
{
    $ProjectName = [Environment]::GetEnvironmentVariable("RAILWAY_PROJECT_NAME") ?? $ProjectName
}

if ($PSBoundParameters.ContainsKey("ProjectId") -eq $false)
{
    $ProjectId = [Environment]::GetEnvironmentVariable("RAILWAY_PROJECT_ID") ?? $ProjectId
}

if ($PSBoundParameters.ContainsKey("EnvironmentName") -eq $false)
{
    $EnvironmentName = [Environment]::GetEnvironmentVariable("RAILWAY_ENVIRONMENT_NAME") ?? $EnvironmentName
}

if ($PSBoundParameters.ContainsKey("RepoSlug") -eq $false)
{
    $RepoSlug = [Environment]::GetEnvironmentVariable("RAILWAY_REPO_SLUG") ?? $RepoSlug
}

if ($PSBoundParameters.ContainsKey("Branch") -eq $false)
{
    $Branch = [Environment]::GetEnvironmentVariable("RAILWAY_REPO_BRANCH") ?? $Branch
}

if ($PSBoundParameters.ContainsKey("WorkspaceId") -eq $false)
{
    $WorkspaceId = [Environment]::GetEnvironmentVariable("RAILWAY_WORKSPACE_ID") ?? $WorkspaceId
}

if ($PSBoundParameters.ContainsKey("DatabaseServiceName") -eq $false)
{
    $DatabaseServiceName = [Environment]::GetEnvironmentVariable("RAILWAY_DATABASE_SERVICE_NAME") ?? $DatabaseServiceName
}

$railwayApiToken = $env:RAILWAY_API_TOKEN
if ([string]::IsNullOrWhiteSpace($railwayApiToken))
{
    throw "RAILWAY_API_TOKEN is required. Create an account or workspace token in Railway and export it before running this script."
}

$railwayCommand = Get-Command railway.exe -ErrorAction SilentlyContinue
if ($CreatePostgres -and $null -eq $railwayCommand)
{
    throw "Railway CLI is required when -CreatePostgres is used. Install it and ensure 'railway.exe' is on PATH."
}

$graphQlUri = "https://backboard.railway.com/graphql/v2"
$personalAgentProjectPath = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\PersonalAgent\PersonalAgent.csproj"))
$personalAgentWebProjectPath = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\PersonalAgent.Web\PersonalAgent.Web.csproj"))

$serviceDefinitions = @(
    @{
        Name = "personalagent-api"
        DockerfilePath = "Dockerfile.personalagent-api"
        HealthcheckPath = "/"
        WatchPatterns = @(
            "AgentPlayground.Contracts/**",
            "PersonalAgent/**",
            "Directory.Build.props",
            "Directory.Packages.props",
            "Dockerfile.personalagent-api"
        )
    },
    @{
        Name = "personalagent-web"
        DockerfilePath = "Dockerfile.personalagent-web"
        HealthcheckPath = "/"
        WatchPatterns = @(
            "AgentPlayground.Contracts/**",
            "PersonalAgent.Web/**",
            "Directory.Build.props",
            "Directory.Packages.props",
            "Dockerfile.personalagent-web"
        )
    },
    @{
        Name = "personalagent-worker"
        DockerfilePath = "Dockerfile.personalagent-worker"
        WatchPatterns = @(
            "AgentPlayground.Contracts/**",
            "PersonalAgent.Worker/**",
            "Directory.Build.props",
            "Directory.Packages.props",
            "Dockerfile.personalagent-worker"
        )
    }
)

function Invoke-RailwayGraphQl
{
    param(
        [string]$Query,
        [hashtable]$Variables = @{}
    )

    $body = @{
        query = $Query
        variables = $Variables
    } | ConvertTo-Json -Depth 20 -Compress

    $response = Invoke-RestMethod -Method Post -Uri $graphQlUri -Headers @{
        Authorization = "Bearer $railwayApiToken"
        "Content-Type" = "application/json"
    } -Body $body

    $responseErrors = $response.PSObject.Properties["errors"]?.Value
    if ($null -ne $responseErrors)
    {
        $message = ($responseErrors | ForEach-Object { $_.message }) -join [Environment]::NewLine
        throw "Railway API error:`n$message"
    }

    return $response.PSObject.Properties["data"]?.Value
}

function Get-ProjectByName
{
    param([string]$Name)

    $data = Invoke-RailwayGraphQl -Query @'
query Projects {
  projects {
    edges {
      node {
        id
        name
      }
    }
  }
}
'@

    return (Get-GraphQlNodes -Connection $data.projects) | Where-Object Name -eq $Name | Select-Object -First 1
}

function Get-GraphQlNodes
{
    param([object]$Connection)

    if ($null -eq $Connection)
    {
        return @()
    }

    $edges = $Connection.PSObject.Properties["edges"]?.Value
    if ($null -eq $edges)
    {
        return @()
    }

    return @($edges | ForEach-Object { $_.PSObject.Properties["node"]?.Value } | Where-Object { $null -ne $_ })
}

function Get-ProjectDetails
{
    param([string]$Id)

    $data = Invoke-RailwayGraphQl -Query @'
query Project($id: String!) {
  project(id: $id) {
    id
    name
    environments {
      edges {
        node {
          id
          name
        }
      }
    }
    services {
      edges {
        node {
          id
          name
        }
      }
    }
  }
}
'@ -Variables @{ id = $Id }

    return $data.project
}

function Ensure-Project
{
    if (-not [string]::IsNullOrWhiteSpace($ProjectId))
    {
        return Get-ProjectDetails -Id $ProjectId
    }

    $existingProject = Get-ProjectByName -Name $ProjectName
    if ($null -ne $existingProject)
    {
        return Get-ProjectDetails -Id $existingProject.id
    }

    $input = @{
        name = $ProjectName
        defaultEnvironmentName = $EnvironmentName
    }

    if (-not [string]::IsNullOrWhiteSpace($WorkspaceId))
    {
        $input.workspaceId = $WorkspaceId
    }

    $data = Invoke-RailwayGraphQl -Query @'
mutation ProjectCreate($input: ProjectCreateInput!) {
  projectCreate(input: $input) {
    id
  }
}
'@ -Variables @{ input = $input }

    return Get-ProjectDetails -Id $data.projectCreate.id
}

function Get-EnvironmentId
{
    param(
        [object]$Project,
        [string]$Name
    )

    $environment = (Get-GraphQlNodes -Connection $Project.environments) | Where-Object Name -eq $Name | Select-Object -First 1
    if ($null -eq $environment)
    {
        throw "Environment '$Name' was not found in project '$($Project.name)'."
    }

    return $environment.id
}

function Connect-ServiceToRepo
{
    param(
        [string]$ServiceId,
        [string]$ServiceName
    )

    if ([string]::IsNullOrWhiteSpace($RepoSlug))
    {
        return
    }

    try
    {
        Invoke-RailwayGraphQl -Query @'
mutation ServiceConnect($id: String!, $input: ServiceConnectInput!) {
  serviceConnect(id: $id, input: $input) {
    id
  }
}
'@ -Variables @{
            id = $ServiceId
            input = @{
                repo = $RepoSlug
                branch = $Branch
            }
        } | Out-Null
    }
    catch
    {
        Write-Warning "Could not connect service '$ServiceName' to repo '$RepoSlug'. Review the service in Railway if it already has a source or if Railway lacks GitHub access."
    }
}

function Ensure-Service
{
    param(
        [object]$Project,
        [hashtable]$Definition
    )

    $existingService = (Get-GraphQlNodes -Connection $Project.services) | Where-Object Name -eq $Definition.Name | Select-Object -First 1
    if ($null -ne $existingService)
    {
        Connect-ServiceToRepo -ServiceId $existingService.id -ServiceName $Definition.Name
        return $existingService
    }

    $input = @{
        projectId = $Project.id
        name = $Definition.Name
    }

    if (-not [string]::IsNullOrWhiteSpace($RepoSlug))
    {
        $input.source = @{ repo = $RepoSlug }
        $input.branch = $Branch
    }

    $data = Invoke-RailwayGraphQl -Query @'
mutation ServiceCreate($input: ServiceCreateInput!) {
  serviceCreate(input: $input) {
    id
    name
  }
}
'@ -Variables @{ input = $input }

    return $data.serviceCreate
}

function Update-ServiceInstance
{
    param(
        [string]$ServiceId,
        [string]$EnvironmentId,
        [hashtable]$Definition
    )

    $input = @{
        dockerfilePath = $Definition.DockerfilePath
        watchPatterns = $Definition.WatchPatterns
        numReplicas = 1
        restartPolicyType = "ON_FAILURE"
        healthcheckTimeout = 300
    }

    if ($Definition.ContainsKey("HealthcheckPath"))
    {
        $input.healthcheckPath = $Definition.HealthcheckPath
    }

    Invoke-RailwayGraphQl -Query @'
mutation ServiceInstanceUpdate($serviceId: String!, $environmentId: String!, $input: ServiceInstanceUpdateInput!) {
  serviceInstanceUpdate(serviceId: $serviceId, environmentId: $environmentId, input: $input)
}
'@ -Variables @{
        serviceId = $ServiceId
        environmentId = $EnvironmentId
        input = $input
    } | Out-Null
}

function Upsert-Variables
{
    param(
        [string]$ProjectIdValue,
        [string]$EnvironmentId,
        [hashtable]$Variables,
        [string]$ServiceId
    )

    if ($Variables.Count -eq 0)
    {
        return
    }

    $input = @{
        projectId = $ProjectIdValue
        environmentId = $EnvironmentId
        variables = $Variables
        skipDeploys = $true
    }

    if (-not [string]::IsNullOrWhiteSpace($ServiceId))
    {
        $input.serviceId = $ServiceId
    }

    Invoke-RailwayGraphQl -Query @'
mutation VariableCollectionUpsert($input: VariableCollectionUpsertInput!) {
  variableCollectionUpsert(input: $input)
}
'@ -Variables @{ input = $input } | Out-Null
}

function Ensure-WebDomain
{
    param(
        [string]$ServiceId,
        [string]$EnvironmentId
    )

    try
    {
        $data = Invoke-RailwayGraphQl -Query @'
mutation ServiceDomainCreate($input: ServiceDomainCreateInput!) {
  serviceDomainCreate(input: $input) {
    domain
  }
}
'@ -Variables @{
            input = @{
                serviceId = $ServiceId
                environmentId = $EnvironmentId
            }
        }

        return $data.serviceDomainCreate.domain
    }
    catch
    {
        Write-Warning "Could not create a Railway public domain for 'personalagent-web'. The service may already have one."
        return $null
    }
}

function Get-SecretVariables
{
    param(
        [string[]]$Names
    )

    $variables = @{}
    foreach ($name in $Names)
    {
        $value = [Environment]::GetEnvironmentVariable($name)
        if (-not [string]::IsNullOrWhiteSpace($value))
        {
            $variables[$name] = $value
        }
    }

    return $variables
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
        return $environmentValue
    }

    foreach ($candidate in $UserSecretCandidates)
    {
        $secretValue = Get-UserSecretValue -ProjectPath $candidate.ProjectPath -Name $candidate.SecretName
        if (-not [string]::IsNullOrWhiteSpace($secretValue))
        {
            return $secretValue
        }
    }

    return $null
}

function Get-SecretVariablesFromMappings
{
    param(
        [object[]]$Mappings
    )

    $variables = @{}
    foreach ($mapping in $Mappings)
    {
        $value = Get-ResolvedSecretValue -EnvironmentVariableName $mapping.EnvironmentVariableName -UserSecretCandidates $mapping.UserSecretCandidates
        if (-not [string]::IsNullOrWhiteSpace($value))
        {
            $variables[$mapping.EnvironmentVariableName] = $value
        }
    }

    return $variables
}

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

$project = Ensure-Project
$environmentId = Get-EnvironmentId -Project $project -Name $EnvironmentName

if ($CreatePostgres)
{
    & $railwayCommand.Source link --project-id $project.id --environment $EnvironmentName | Out-Null
    if ($LASTEXITCODE -ne 0)
    {
        throw "Failed to link Railway CLI to project '$($project.name)'."
    }

    $existingDatabase = (Get-GraphQlNodes -Connection $project.services) | Where-Object Name -eq $DatabaseServiceName | Select-Object -First 1
    if ($null -eq $existingDatabase)
    {
        & $railwayCommand.Source add --database postgre-sql | Out-Null
        if ($LASTEXITCODE -ne 0)
        {
            throw @"
Failed to create Railway PostgreSQL service automatically.

Your installed Railway CLI requires extra context for database provisioning and does not expose a non-interactive workspace flag here.

Minimum manual step:
1. Create a Railway PostgreSQL service in the same project/environment and name it '$DatabaseServiceName'
2. Re-run: pwsh -NoProfile -File .\scripts\setup-railway.ps1

If you prefer a different service name, set RAILWAY_DATABASE_SERVICE_NAME in .env.railway first.
"@
        }

        $project = Get-ProjectDetails -Id $project.id
    }
}

$serviceMap = @{}
foreach ($definition in $serviceDefinitions)
{
    $service = Ensure-Service -Project $project -Definition $definition
    Update-ServiceInstance -ServiceId $service.id -EnvironmentId $environmentId -Definition $definition
    $serviceMap[$definition.Name] = $service.id
}

Upsert-Variables -ProjectIdValue $project.id -EnvironmentId $environmentId -Variables @{
    MESSAGING_CONNECTION_STRING = "`${{${DatabaseServiceName}.DATABASE_URL}}"
    MESSAGING_SCHEMA = "transport"
}

Upsert-Variables -ProjectIdValue $project.id -EnvironmentId $environmentId -ServiceId $serviceMap["personalagent-web"] -Variables @{
    PERSONAL_AGENT_API_BASE_URL = "http://`${{personalagent-api.RAILWAY_PRIVATE_DOMAIN}}:`${{personalagent-api.PORT}}"
    INTERNAL_API_KEY = "`${{personalagent-api.INTERNAL_API_KEY}}"
}

$apiSecrets = Get-SecretVariablesFromMappings -Mappings @(
    @{
        EnvironmentVariableName = "OPENAI_API_KEY"
        UserSecretCandidates = @(
            @{ ProjectPath = $personalAgentProjectPath; SecretName = "OpenAI:ApiKey" }
        )
    },
    @{
        EnvironmentVariableName = "INTERNAL_API_KEY"
        UserSecretCandidates = @(
            @{ ProjectPath = $personalAgentProjectPath; SecretName = "Security:InternalApiKey" },
            @{ ProjectPath = $personalAgentProjectPath; SecretName = "InternalApiKey" }
        )
    }
)

$webSecrets = Get-SecretVariablesFromMappings -Mappings @(
    @{
        EnvironmentVariableName = "GITHUB_CLIENT_ID"
        UserSecretCandidates = @(
            @{ ProjectPath = $personalAgentWebProjectPath; SecretName = "Authentication:Schemes:GitHub:ClientId" }
        )
    },
    @{
        EnvironmentVariableName = "GITHUB_CLIENT_SECRET"
        UserSecretCandidates = @(
            @{ ProjectPath = $personalAgentWebProjectPath; SecretName = "Authentication:Schemes:GitHub:ClientSecret" }
        )
    },
    @{
        EnvironmentVariableName = "GITHUB_ALLOWED_USERS"
        UserSecretCandidates = @(
            @{ ProjectPath = $personalAgentWebProjectPath; SecretName = "Authentication:Schemes:GitHub:AllowedUsers" }
        )
    },
    @{
        EnvironmentVariableName = "GITHUB_CALLBACK_PATH"
        UserSecretCandidates = @(
            @{ ProjectPath = $personalAgentWebProjectPath; SecretName = "Authentication:Schemes:GitHub:CallbackPath" }
        )
    }
)

Upsert-Variables -ProjectIdValue $project.id -EnvironmentId $environmentId -ServiceId $serviceMap["personalagent-api"] -Variables $apiSecrets
Upsert-Variables -ProjectIdValue $project.id -EnvironmentId $environmentId -ServiceId $serviceMap["personalagent-web"] -Variables $webSecrets

$webDomain = Ensure-WebDomain -ServiceId $serviceMap["personalagent-web"] -EnvironmentId $environmentId

Write-Host "Railway project ready."
Write-Host "Project: $($project.name) [$($project.id)]"
Write-Host "Environment: $EnvironmentName [$environmentId]"
Write-Host "Services: personalagent-api, personalagent-web, personalagent-worker"

if (-not [string]::IsNullOrWhiteSpace($webDomain))
{
    Write-Host "Web domain: https://$webDomain"
    Write-Host "GitHub OAuth callback: https://$webDomain/signin-github"
}

if (-not [string]::IsNullOrWhiteSpace($RepoSlug))
{
    Write-Host "Repo-backed services configured from '$RepoSlug' on branch '$Branch'."
}
else
{
    Write-Host "Services were created without a repo source. Use scripts/deploy-railway.ps1 to upload local code."
}

if (-not $apiSecrets.ContainsKey("OPENAI_API_KEY"))
{
    Write-Warning "OPENAI_API_KEY was not found in .env.railway or PersonalAgent user secrets, so it was not set in Railway."
}

if (-not $webSecrets.ContainsKey("GITHUB_CLIENT_ID") -or -not $webSecrets.ContainsKey("GITHUB_CLIENT_SECRET"))
{
    Write-Warning "GitHub OAuth secrets were not found in .env.railway or PersonalAgent.Web user secrets, so they were not set in Railway."
}
