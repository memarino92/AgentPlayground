Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-SnapshotDocker {
    param([string[]]$Arguments, [string]$Operation = 'Docker operation', [string]$InputText)
    # Never echo native stderr: database connection errors can contain credentials/data.
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = if ($PSBoundParameters.ContainsKey('InputText')) { $InputText | & docker @Arguments 2>&1 } else { & docker @Arguments 2>&1 }
        $exitCode = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $previousPreference }
    if ($exitCode -ne 0) { throw "$Operation failed (exit $exitCode). Native output withheld because it may contain private data." }
    return ($output | ForEach-Object { $_.ToString() }) -join "`n"
}

function Assert-LocalSnapshotDocker {
    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { throw 'Docker is required.' }
    # Pin every invocation to this context; reject DOCKER_HOST overrides and remote daemons.
    $endpoint = [Environment]::GetEnvironmentVariable('DOCKER_HOST')
    if ([string]::IsNullOrWhiteSpace($endpoint)) {
        $context = Invoke-SnapshotDocker -Arguments @('context','show') -Operation 'Read Docker context'
        $endpoint = Invoke-SnapshotDocker -Arguments @('context','inspect',$context,'--format','{{.Endpoints.docker.Host}}') -Operation 'Read Docker endpoint'
    }
    if ($endpoint -notmatch '^(npipe:////\./pipe/|unix:///)' -or $endpoint -match '[\r\n]') {
        throw 'Snapshots require a local Docker daemon (named pipe or Unix socket); remote Docker endpoints are refused.'
    }
    return $endpoint
}

function ConvertFrom-SnapshotDatabaseUrl {
    param([string]$Value)
    $uri = $null
    if (-not [Uri]::TryCreate($Value, [UriKind]::Absolute, [ref]$uri) -or $uri.Scheme -notin @('postgres','postgresql')) {
        throw 'The source environment variable must contain a PostgreSQL URL.'
    }
    if ($uri.Fragment -or -not $uri.Host -or $uri.Host.Contains(',')) { throw 'Use a single-host PostgreSQL URL without a fragment.' }
    $userinfo = $uri.UserInfo.Split(@(':'), 2)
    $database = [Uri]::UnescapeDataString($uri.AbsolutePath.TrimStart('/'))
    if ($userinfo.Count -ne 2 -or -not $userinfo[0] -or -not $database -or $database.Contains('/')) { throw 'The PostgreSQL URL must include a user, password, and database.' }
    $values = @{
        PGHOST = $uri.DnsSafeHost
        PGPORT = if ($uri.Port -gt 0) { [string]$uri.Port } else { '5432' }
        PGUSER = [Uri]::UnescapeDataString($userinfo[0])
        PGPASSWORD = [Uri]::UnescapeDataString($userinfo[1])
        PGDATABASE = $database
        PGCONNECT_TIMEOUT = '15'
        PGSSLMODE = 'prefer'
        PGAPPNAME = 'digital-garden-snapshot'
    }
    foreach ($part in $uri.Query.TrimStart('?').Split('&', [StringSplitOptions]::RemoveEmptyEntries)) {
        $pair = $part.Split(@('='), 2)
        if ($pair.Count -ne 2 -or $pair[0] -ne 'sslmode') { throw 'Only sslmode is supported in source URL query parameters; unknown options are refused.' }
        $sslmode = [Uri]::UnescapeDataString($pair[1])
        if ($sslmode -notin @('disable','allow','prefer','require','verify-ca','verify-full')) { throw 'Unsupported sslmode.' }
        $values.PGSSLMODE = $sslmode
    }
    foreach ($value in $values.Values) { if ($value -match '[\r\n\x00]') { throw 'Connection values cannot contain control characters.' } }
    return $values
}

function Invoke-SnapshotClient {
    param([string]$Endpoint, [string]$Image, [hashtable]$Connection, [string[]]$Command, [string[]]$Mounts = @(), [string]$Network = 'bridge')
    $prior = @{}
    $arguments = @('--host',$Endpoint,'run','--rm','--network',$Network,'--add-host','host.docker.internal:host-gateway')
    try {
        foreach ($key in $Connection.Keys) {
            $prior[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
            [Environment]::SetEnvironmentVariable($key, $Connection[$key], 'Process')
            $arguments += @('--env',$key)
        }
        foreach ($mount in $Mounts) { $arguments += @('--mount',$mount) }
        return Invoke-SnapshotDocker -Arguments ($arguments + @($Image) + $Command) -Operation 'PostgreSQL snapshot client'
    }
    finally { foreach ($key in $prior.Keys) { [Environment]::SetEnvironmentVariable($key, $prior[$key], 'Process') } }
}

function Get-SnapshotMount {
    param([string]$Source, [string]$Target, [switch]$ReadOnly)
    if ($Source -match '[,\r\n]' -or $Target -match '[,\r\n]') { throw 'Snapshot mount paths cannot contain commas or newlines.' }
    $suffix = if ($ReadOnly) { ',readonly' } else { '' }
    return "type=bind,source=$Source,target=$Target$suffix"
}

function Write-SnapshotJson {
    param([string]$Path, $Value)
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 12), (New-Object Text.UTF8Encoding($false)))
}

function Read-SnapshotManifest {
    param([string]$Path)
    $archive = (Resolve-Path -LiteralPath $Path -ErrorAction Stop).Path
    if (-not [IO.File]::Exists($archive)) { throw 'Snapshot must be a file.' }
    $manifestPath = "$archive.json"
    if (-not [IO.File]::Exists($manifestPath)) { throw 'Snapshot manifest is missing; an archive and its .json manifest are required.' }
    $manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
    if ($manifest.FormatVersion -ne 1 -or $manifest.Format -ne 'PostgreSQLCustom' -or $manifest.Mode -ne 'Full') { throw 'Unsupported snapshot manifest.' }
    if ($manifest.Sha256 -notmatch '^[a-fA-F0-9]{64}$' -or (Get-Item -LiteralPath $archive).Length -ne $manifest.SizeBytes) { throw 'Snapshot size or checksum metadata is invalid.' }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $manifest.Sha256) { throw 'Snapshot checksum mismatch.' }
    return [pscustomobject]@{ Archive = $archive; Manifest = $manifest }
}

function Invoke-RestoreSql {
    param([string]$Endpoint, [string]$Container, [string]$Sql)
    return Invoke-SnapshotDocker -Arguments @('--host',$Endpoint,'exec','-i',$Container,'psql','-X','-U','postgres','-d','garden','--set','ON_ERROR_STOP=1','--tuples-only','--no-align','--quiet') -InputText $Sql -Operation 'Restored database query'
}

function Assert-SnapshotConfigurationKey {
    param([object[]]$Settings, [string]$Key)
    $secrets = @($Settings | Where-Object { $_.is_secret })
    if (-not $secrets.Count) { return 0 }
    try { $master = [Convert]::FromBase64String($Key) } catch { throw 'The snapshot configuration key must be valid base64.' }
    if ($master.Length -ne 32) { throw 'The snapshot configuration key must contain 32 bytes.' }
    $derive = New-Object Security.Cryptography.HMACSHA256 -ArgumentList (, $master)
    try {
        $authenticationKey = $derive.ComputeHash([Text.Encoding]::UTF8.GetBytes('configuration-authentication'))
        $encryptionKey = $derive.ComputeHash([Text.Encoding]::UTF8.GetBytes('configuration-encryption'))
    }
    finally { $derive.Dispose() }
    foreach ($setting in $secrets) {
        try {
            $segments = $setting.value.Split(':')
            if ($segments.Count -ne 4 -or $segments[0] -ne 'v1') { throw 'Unsupported encrypted format.' }
            $iv = [Convert]::FromBase64String($segments[1])
            $ciphertext = [Convert]::FromBase64String($segments[2])
            $tag = [Convert]::FromBase64String($segments[3])
            [byte[]]$data = @([Text.Encoding]::UTF8.GetBytes("$($setting.scope)`n$($setting.key)`n")) + @($iv) + @($ciphertext)
            $hmac = New-Object Security.Cryptography.HMACSHA256 -ArgumentList (, $authenticationKey)
            try { $expected = $hmac.ComputeHash($data) } finally { $hmac.Dispose() }
            $difference = $expected.Length -bxor $tag.Length
            for ($index = 0; $index -lt [Math]::Min($expected.Length,$tag.Length); $index++) { $difference = $difference -bor ($expected[$index] -bxor $tag[$index]) }
            if ($difference -ne 0) { throw 'Authentication failed.' }
            $aes = [Security.Cryptography.Aes]::Create()
            try {
                $aes.Key = $encryptionKey
                $aes.IV = $iv
                $decryptor = $aes.CreateDecryptor()
                try { $plaintext = $decryptor.TransformFinalBlock($ciphertext,0,$ciphertext.Length); [Array]::Clear($plaintext,0,$plaintext.Length) }
                finally { $decryptor.Dispose() }
            }
            finally { $aes.Dispose() }
        }
        catch { throw 'Configuration decryption failed. Check the snapshot key and encrypted data; secret details were withheld.' }
    }
    return $secrets.Count
}

function Initialize-SnapshotImage {
    param([string]$Endpoint, [string]$Image)
    try { $null = Invoke-SnapshotDocker -Arguments @('--host',$Endpoint,'image','inspect',$Image) -Operation 'Inspect snapshot image' }
    catch { $null = Invoke-SnapshotDocker -Arguments @('--host',$Endpoint,'pull',$Image) -Operation 'Pull snapshot image' }
}
