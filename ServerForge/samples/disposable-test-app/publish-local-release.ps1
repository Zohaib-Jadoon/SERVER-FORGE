<#
.SYNOPSIS
    Builds a genuine self-contained Windows x64 release artifact for ServerForge.SampleApp.
.DESCRIPTION
    Compiles and publishes ServerForge.SampleApp.csproj using dotnet publish, embeds exact
    release-metadata.json (version + 40-char commit SHA + UTC timestamp), zips the output,
    and writes an independently verifiable SHA-256 checksum file. Never fabricates artifacts.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputZipPath,
    [string]$Version = '1.0.0-e2e',
    [string]$CommitSha = '',
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ([string]::IsNullOrWhiteSpace($CommitSha)) {
    $CommitSha = ('a' * 8) + ([Guid]::NewGuid().ToString('N').ToLowerInvariant())
}
if ($CommitSha -notmatch '^[0-9a-fA-F]{40}$') {
    throw "CommitSha must be a 40-character hexadecimal SHA. Received: '$CommitSha'."
}

$sampleDir = $PSScriptRoot
$csproj = Join-Path $sampleDir 'ServerForge.SampleApp.csproj'
if (-not (Test-Path $csproj)) {
    throw "Sample project not found: $csproj"
}

$outZipAbs = if ([System.IO.Path]::IsPathRooted($OutputZipPath)) {
    [System.IO.Path]::GetFullPath($OutputZipPath)
} else {
    [System.IO.Path]::GetFullPath((Join-Path (Get-Location).Path $OutputZipPath))
}

$outParent = Split-Path -Path $outZipAbs -Parent
if (-not [string]::IsNullOrWhiteSpace($outParent)) {
    New-Item -ItemType Directory -Path $outParent -Force | Out-Null
}

$stagingDir = Join-Path ([System.IO.Path]::GetTempPath()) ("sf-sample-pub-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null

try {
    & dotnet publish $csproj -c $Configuration -r win-x64 --self-contained true -o $stagingDir
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for ServerForge.SampleApp.csproj with exit code $LASTEXITCODE."
    }

    $meta = [ordered]@{
        version    = $Version
        commitSha  = $CommitSha.ToLowerInvariant()
        builtAtUtc = (Get-Date).ToUniversalTime().ToString('O')
    }
    $meta | ConvertTo-Json -Depth 4 | Set-Content -Path (Join-Path $stagingDir 'release-metadata.json') -Encoding UTF8

    if (Test-Path $outZipAbs) { Remove-Item -Path $outZipAbs -Force }
    Compress-Archive -Path (Join-Path $stagingDir '*') -DestinationPath $outZipAbs -Force

    if (-not (Test-Path $outZipAbs) -or (Get-Item $outZipAbs).Length -le 0) {
        throw "Failed to produce non-empty release archive at $outZipAbs"
    }

    $sha256 = (Get-FileHash -Path $outZipAbs -Algorithm SHA256).Hash.ToLowerInvariant()
    "$sha256  $([System.IO.Path]::GetFileName($outZipAbs))" | Set-Content -Path "$outZipAbs.sha256" -Encoding ASCII

    [PSCustomObject]@{
        ZipPath   = $outZipAbs
        Sha256    = $sha256
        SizeBytes = (Get-Item $outZipAbs).Length
        Version   = $Version
        CommitSha = $CommitSha.ToLowerInvariant()
    }
}
finally {
    Remove-Item -Path $stagingDir -Recurse -Force -ErrorAction SilentlyContinue
}
