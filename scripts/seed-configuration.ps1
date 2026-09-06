param(
    [string]$ValuesPath = (Join-Path $PSScriptRoot 'seed-configuration.values.ps1'),
    [string]$OutputPath = (Join-Path $PSScriptRoot 'seed-configuration.generated.sql'),
    [switch]$Apply
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $ValuesPath))
{
    throw "Create '$ValuesPath' from seed-configuration.values.ps1.example and fill in the Railway values first."
}

. $ValuesPath

function Assert-ConfiguredValue([string]$Name, [string]$Value)
{
    if ([string]::IsNullOrWhiteSpace($Value) -or $Value.StartsWith('<'))
    {
        throw "Set `$${Name} in '$ValuesPath'."
    }
}

function Get-DerivedKey([byte[]]$MasterKey, [string]$Purpose)
{
    $hmac = New-Object System.Security.Cryptography.HMACSHA256 -ArgumentList (, $MasterKey)
    try
    {
        return $hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes($Purpose))
    }
    finally
    {
        $hmac.Dispose()
    }
}

function Protect-ConfigurationValue([string]$Plaintext, [string]$Scope, [string]$Key, [byte[]]$MasterKey)
{
    $aes = [Security.Cryptography.Aes]::Create()
    try
    {
        $aes.Key = Get-DerivedKey $MasterKey 'configuration-encryption'
        $aes.Mode = [Security.Cryptography.CipherMode]::CBC
        $aes.Padding = [Security.Cryptography.PaddingMode]::PKCS7
        $aes.GenerateIV()
        $encryptor = $aes.CreateEncryptor()
        try
        {
            $plaintextBytes = [Text.Encoding]::UTF8.GetBytes($Plaintext)
            $ciphertext = $encryptor.TransformFinalBlock($plaintextBytes, 0, $plaintextBytes.Length)
        }
        finally
        {
            $encryptor.Dispose()
        }

        [byte[]]$prefix = [Text.Encoding]::UTF8.GetBytes("$Scope`n$Key`n")
        [byte[]]$authenticatedData = @($prefix) + @($aes.IV) + @($ciphertext)
        $authenticationKey = Get-DerivedKey $MasterKey 'configuration-authentication'
        $hmac = New-Object System.Security.Cryptography.HMACSHA256 -ArgumentList (, $authenticationKey)
        try
        {
            $tag = $hmac.ComputeHash($authenticatedData)
        }
        finally
        {
            $hmac.Dispose()
        }

        return 'v1:{0}:{1}:{2}' -f [Convert]::ToBase64String($aes.IV), [Convert]::ToBase64String($ciphertext), [Convert]::ToBase64String($tag)
    }
    finally
    {
        $aes.Dispose()
    }
}

function ConvertTo-SqlLiteral([string]$Value)
{
    return "'$($Value.Replace("'", "''"))'"
}

Assert-ConfiguredValue 'ConfigEncryptionKey' $ConfigEncryptionKey
Assert-ConfiguredValue 'InternalApiKey' $InternalApiKey
Assert-ConfiguredValue 'ActorSigningKey' $ActorSigningKey
Assert-ConfiguredValue 'PersonalAgentApiBaseUrl' $PersonalAgentApiBaseUrl
Assert-ConfiguredValue 'OpenAiApiKey' $OpenAiApiKey
Assert-ConfiguredValue 'GitHubClientId' $GitHubClientId
Assert-ConfiguredValue 'GitHubClientSecret' $GitHubClientSecret
Assert-ConfiguredValue 'GitHubAllowedUsers' $GitHubAllowedUsers
Assert-ConfiguredValue 'GoogleClientId' $GoogleClientId
Assert-ConfiguredValue 'GoogleClientSecret' $GoogleClientSecret
Assert-ConfiguredValue 'GoogleAllowedEmails' $GoogleAllowedEmails
Assert-ConfiguredValue 'AssemblyAiApiKey' $AssemblyAiApiKey
Assert-ConfiguredValue 'AllowedOrigins' $AllowedOrigins

try
{
    [byte[]]$masterKey = [Convert]::FromBase64String($ConfigEncryptionKey)
}
catch
{
    throw 'ConfigEncryptionKey must be valid base64.'
}
if ($masterKey.Length -ne 32) { throw 'ConfigEncryptionKey must decode to exactly 32 bytes.' }

$settings = @(
    @{ Scope = 'Api'; Key = 'OpenAI:ApiKey'; Value = $OpenAiApiKey; Secret = $true },
    @{ Scope = 'Api'; Key = 'Security:InternalApiKey'; Value = $InternalApiKey; Secret = $true },
    @{ Scope = 'Api'; Key = 'Security:ActorSigningKey'; Value = $ActorSigningKey; Secret = $true },
    @{ Scope = 'Api'; Key = 'Security:AllowedOrigins:0'; Value = $AllowedOrigins; Secret = $false },
    @{ Scope = 'Api'; Key = 'Tavily:ApiKey'; Value = $TavilyApiKey; Secret = $true },
    @{ Scope = 'Api'; Key = 'Tavily:McpUrl'; Value = $TavilyMcpUrl; Secret = $false },
    @{ Scope = 'Api'; Key = 'Tavily:EnableWebSearch'; Value = $EnableWebSearch; Secret = $false },
    @{ Scope = 'Api'; Key = 'PushNotifications:Enabled'; Value = $PushNotificationsEnabled; Secret = $false },
    @{ Scope = 'Api'; Key = 'PushNotifications:FirebaseProjectId'; Value = $FirebaseProjectId; Secret = $false },
    @{ Scope = 'Api'; Key = 'PushNotifications:ServiceAccountJsonBase64'; Value = $FirebaseServiceAccountJsonBase64; Secret = $true },
    @{ Scope = 'Api'; Key = 'PushNotifications:AndroidChannelId'; Value = $AndroidPushChannelId; Secret = $false },

    @{ Scope = 'Web'; Key = 'PersonalAgentApi:BaseUrl'; Value = $PersonalAgentApiBaseUrl; Secret = $false },
    @{ Scope = 'Web'; Key = 'PersonalAgentApi:InternalApiKey'; Value = $InternalApiKey; Secret = $true },
    @{ Scope = 'Web'; Key = 'PersonalAgentApi:ActorSigningKey'; Value = $ActorSigningKey; Secret = $true },
    @{ Scope = 'Web'; Key = 'Authentication:Schemes:GitHub:ClientId'; Value = $GitHubClientId; Secret = $false },
    @{ Scope = 'Web'; Key = 'Authentication:Schemes:GitHub:ClientSecret'; Value = $GitHubClientSecret; Secret = $true },
    @{ Scope = 'Web'; Key = 'Authentication:Schemes:GitHub:AllowedUsers'; Value = $GitHubAllowedUsers; Secret = $false },
    @{ Scope = 'Web'; Key = 'Authentication:Schemes:Google:ClientId'; Value = $GoogleClientId; Secret = $false },
    @{ Scope = 'Web'; Key = 'Authentication:Schemes:Google:ClientSecret'; Value = $GoogleClientSecret; Secret = $true },
    @{ Scope = 'Web'; Key = 'Authentication:Schemes:Google:AllowedEmails'; Value = $GoogleAllowedEmails; Secret = $false },

    @{ Scope = 'Worker'; Key = 'PersonalAgentApi:BaseUrl'; Value = $PersonalAgentApiBaseUrl; Secret = $false },
    @{ Scope = 'Worker'; Key = 'PersonalAgentApi:InternalApiKey'; Value = $InternalApiKey; Secret = $true },
    @{ Scope = 'Worker'; Key = 'PersonalAgentApi:ActorSigningKey'; Value = $ActorSigningKey; Secret = $true },
    @{ Scope = 'Worker'; Key = 'AssemblyAi:ApiKey'; Value = $AssemblyAiApiKey; Secret = $true },
    @{ Scope = 'Worker'; Key = 'GitHub:PersonalAccessToken'; Value = $GitHubPat; Secret = $true },
    @{ Scope = 'Worker'; Key = 'GitHub:RepoOwner'; Value = $GitHubOwner; Secret = $false },
    @{ Scope = 'Worker'; Key = 'GitHub:RepoName'; Value = $GitHubRepo; Secret = $false },
    @{ Scope = 'Worker'; Key = 'GitHub:Branch'; Value = $GitHubBranch; Secret = $false },
    @{ Scope = 'Worker'; Key = 'GitHub:JournalPath'; Value = $GitHubJournalPath; Secret = $false }
)

$sql = New-Object Text.StringBuilder
[void]$sql.AppendLine('BEGIN;')
[void]$sql.AppendLine('CREATE SCHEMA IF NOT EXISTS app;')
[void]$sql.AppendLine(@'
CREATE TABLE IF NOT EXISTS app.configuration_settings
(
    scope text NOT NULL,
    key text NOT NULL,
    value text NOT NULL,
    is_secret boolean NOT NULL,
    is_active boolean NOT NULL DEFAULT true,
    updated_at timestamptz NOT NULL,
    PRIMARY KEY (scope, key)
);
'@)

$seeded = 0
foreach ($setting in $settings)
{
    $scopeSql = ConvertTo-SqlLiteral $setting.Scope
    $keySql = ConvertTo-SqlLiteral $setting.Key
    if ([string]::IsNullOrWhiteSpace([string]$setting.Value))
    {
        [void]$sql.AppendLine("DELETE FROM app.configuration_settings WHERE scope = $scopeSql AND key = $keySql;")
        continue
    }
    $storedValue = if ($setting.Secret)
    {
        Protect-ConfigurationValue ([string]$setting.Value) $setting.Scope $setting.Key $masterKey
    }
    else
    {
        [string]$setting.Value
    }
    $valueSql = ConvertTo-SqlLiteral $storedValue
    $secretSql = if ($setting.Secret) { 'true' } else { 'false' }
    [void]$sql.AppendLine("INSERT INTO app.configuration_settings (scope, key, value, is_secret, is_active, updated_at) VALUES ($scopeSql, $keySql, $valueSql, $secretSql, true, now()) ON CONFLICT (scope, key) DO UPDATE SET value = EXCLUDED.value, is_secret = EXCLUDED.is_secret, is_active = true, updated_at = now();")
    $seeded++
}
[void]$sql.AppendLine('COMMIT;')

$resolvedOutputPath = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = [IO.Path]::GetDirectoryName($resolvedOutputPath)
if (-not [string]::IsNullOrWhiteSpace($outputDirectory) -and -not [IO.Directory]::Exists($outputDirectory))
{
    [void][IO.Directory]::CreateDirectory($outputDirectory)
}
[IO.File]::WriteAllText($resolvedOutputPath, $sql.ToString(), (New-Object Text.UTF8Encoding($false)))

Write-Host "Generated '$resolvedOutputPath' with $seeded configuration values."
Write-Host 'Paste the SQL into the PostgreSQL console, then securely delete the generated file.'

if ($Apply)
{
    if ([string]::IsNullOrWhiteSpace($DatabaseUrl))
    {
        $DatabaseUrl = [Environment]::GetEnvironmentVariable('DATABASE_URL')
    }
    Assert-ConfiguredValue 'DatabaseUrl' $DatabaseUrl
    $sql.ToString() | & docker run --rm -i postgres:18 psql $DatabaseUrl -v ON_ERROR_STOP=1 --quiet
    if ($LASTEXITCODE -ne 0) { throw 'Configuration seed failed.' }
    Write-Host 'Applied the generated configuration to PostgreSQL.'
}

Write-Host 'Set DATABASE_URL and CONFIG_ENCRYPTION_KEY on API, Web, and Worker, then remove their migrated configuration variables.'
