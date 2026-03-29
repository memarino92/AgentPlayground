param(
    [string]$EnvFilePath = ".env.railway",
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$dotnetCommand = Get-Command dotnet.exe -ErrorAction SilentlyContinue
if ($null -eq $dotnetCommand)
{
    throw "The .NET SDK is required. Install it and ensure 'dotnet.exe' is on PATH."
}

$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$personalAgentProjectPath = [System.IO.Path]::GetFullPath((Join-Path $projectRoot "PersonalAgent\PersonalAgent.csproj"))
$exampleEnvFilePath = [System.IO.Path]::GetFullPath((Join-Path $projectRoot ".env.railway.example"))
$resolvedEnvFilePath = [System.IO.Path]::GetFullPath((Join-Path $projectRoot $EnvFilePath))

if (-not (Test-Path -LiteralPath $personalAgentProjectPath))
{
    throw "Could not find PersonalAgent project at '$personalAgentProjectPath'."
}

function Get-UserSecretValue
{
    param(
        [string]$ProjectPath,
        [string]$Name
    )

    $lines = & $dotnetCommand.Source user-secrets list --project $ProjectPath 2>$null
    if ($LASTEXITCODE -ne 0)
    {
        return $null
    }

    foreach ($line in $lines)
    {
        $separator = $line.IndexOf(" = ", [System.StringComparison]::Ordinal)
        if ($separator -lt 0)
        {
            continue
        }

        $key = $line.Substring(0, $separator).Trim()
        if ($key -ne $Name)
        {
            continue
        }

        return $line.Substring($separator + 3).Trim()
    }

    return $null
}

function New-InternalApiKey
{
    $bytes = [byte[]]::new(32)
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)

    return [Convert]::ToBase64String($bytes)
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_')
}

function Set-DotEnvValue
{
    param(
        [string]$Path,
        [string]$Name,
        [string]$Value
    )

    $lines = if (Test-Path -LiteralPath $Path)
    {
        [System.Collections.Generic.List[string]]([System.IO.File]::ReadAllLines($Path))
    }
    else
    {
        [System.Collections.Generic.List[string]]::new()
    }

    $updated = $false
    for ($index = 0; $index -lt $lines.Count; $index++)
    {
        if (-not $lines[$index].StartsWith("$Name=", [System.StringComparison]::Ordinal))
        {
            continue
        }

        $lines[$index] = "$Name=$Value"
        $updated = $true
        break
    }

    if (-not $updated)
    {
        if ($lines.Count -gt 0 -and -not [string]::IsNullOrWhiteSpace($lines[$lines.Count - 1]))
        {
            $lines.Add("")
        }

        $lines.Add("$Name=$Value")
    }

    [System.IO.File]::WriteAllLines($Path, $lines)
}

$existingKey = Get-UserSecretValue -ProjectPath $personalAgentProjectPath -Name "Security:InternalApiKey"
if (-not $Force -and -not [string]::IsNullOrWhiteSpace($existingKey))
{
    throw "Security:InternalApiKey already exists in PersonalAgent user secrets. Re-run with -Force to rotate it."
}

$internalApiKey = New-InternalApiKey

& $dotnetCommand.Source user-secrets set "Security:InternalApiKey" $internalApiKey --project $personalAgentProjectPath | Out-Null
if ($LASTEXITCODE -ne 0)
{
    throw "Failed to write Security:InternalApiKey to PersonalAgent user secrets."
}

if (-not (Test-Path -LiteralPath $resolvedEnvFilePath) -and (Test-Path -LiteralPath $exampleEnvFilePath))
{
    [System.IO.File]::Copy($exampleEnvFilePath, $resolvedEnvFilePath)
}

Set-DotEnvValue -Path $resolvedEnvFilePath -Name "INTERNAL_API_KEY" -Value $internalApiKey

Write-Host "Internal API key generated."
Write-Host "Updated PersonalAgent user secrets: Security:InternalApiKey"
Write-Host "Updated env file: $resolvedEnvFilePath"
Write-Host "Run 'pwsh -NoProfile -File .\scripts\check-railway-env.ps1' to verify the full Railway configuration."
