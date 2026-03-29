Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Import-DotEnv
{
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [switch]$Overwrite
    )

    if (-not (Test-Path -LiteralPath $Path))
    {
        return
    }

    foreach ($rawLine in [System.IO.File]::ReadAllLines($Path))
    {
        $line = $rawLine.Trim()
        if ([string]::IsNullOrWhiteSpace($line) -or $line.StartsWith('#'))
        {
            continue
        }

        $separatorIndex = $line.IndexOf('=')
        if ($separatorIndex -lt 1)
        {
            continue
        }

        $name = $line.Substring(0, $separatorIndex).Trim()
        if ([string]::IsNullOrWhiteSpace($name))
        {
            continue
        }

        $value = $line.Substring($separatorIndex + 1).Trim()
        if ($value.Length -ge 2)
        {
            $firstChar = $value[0]
            $lastChar = $value[$value.Length - 1]
            if (($firstChar -eq '"' -and $lastChar -eq '"') -or ($firstChar -eq "'" -and $lastChar -eq "'"))
            {
                $value = $value.Substring(1, $value.Length - 2)
            }
        }

        if (-not $Overwrite -and -not [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name)))
        {
            continue
        }

        [Environment]::SetEnvironmentVariable($name, $value)
    }
}
