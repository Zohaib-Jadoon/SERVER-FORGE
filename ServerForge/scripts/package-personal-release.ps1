#Requires -Version 5.1
<#
.SYNOPSIS
    Assembles and cryptographically verifies the final ServerForge Personal Windows x64 distribution ZIP:
    ServerForge-Personal-Windows-x64-v1.0.0.zip
.DESCRIPTION
    Stages the verified Windows installers (ServerForge-Desktop-Setup.exe & ServerForge-Agent-Setup.exe)
    alongside START_HERE.txt, INSTALLATION_GUIDE.md, VERSION.txt, and SHA256SUMS.txt inside the
    ServerForge-Personal-Windows-x64-v1.0.0/ directory, validates PE executable headers and minimum
    sizes so source files can never masquerade as installers, creates the distribution ZIP, and
    re-reads the ZIP archive to verify every member and SHA-256 digest.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$InstallerDir,
    [string]$OutputDir = "",
    [string]$Version = "1.0.0",
    [string]$SourceCommit = "f10ae9218ce0ddcc8f3453b0330cae05fe979242",
    [string]$SourceCiRunUrl = "https://github.com/Zohaib-Jadoon/SERVER-FORGE/actions/runs/44870755690",
    [string]$PackagedCommit = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Assert-ValidWindowsInstallerExe {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][long]$MinBytes
    )
    if (-not (Test-Path $FilePath)) {
        throw "Required Windows installer executable not found: '$FilePath'."
    }
    $item = Get-Item $FilePath
    if ($item.Length -lt $MinBytes) {
        throw "Installer '$($item.Name)' is unexpectedly small ($($item.Length) bytes; expected >= $MinBytes bytes). Refusing to package a stub or source file."
    }
    $stream = [System.IO.File]::OpenRead($FilePath)
    try {
        $header = New-Object byte[] 2
        $read = $stream.Read($header, 0, 2)
        if ($read -ne 2 -or $header[0] -ne 0x4D -or $header[1] -ne 0x5A) {
            throw "Installer '$($item.Name)' does not have a valid Windows PE ('MZ') executable header."
        }
    }
    finally {
        $stream.Dispose()
    }
}

$RepoServerForgeRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$ReleaseDocsDir = Join-Path $RepoServerForgeRoot "release\personal-v$Version"
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $RepoServerForgeRoot "artifacts\release"
}

$ResolvedInstallerDir = (Resolve-Path $InstallerDir).Path
$DesktopExeSrc = Join-Path $ResolvedInstallerDir "ServerForge-Desktop-Setup.exe"
$AgentExeSrc   = Join-Path $ResolvedInstallerDir "ServerForge-Agent-Setup.exe"
$SourceSums    = Join-Path $ResolvedInstallerDir "SHA256SUMS.txt"

# Verify both EXEs are real self-contained Windows PE installers (> 10 MB each)
Assert-ValidWindowsInstallerExe -FilePath $DesktopExeSrc -MinBytes (10MB)
Assert-ValidWindowsInstallerExe -FilePath $AgentExeSrc   -MinBytes (10MB)

# If source SHA256SUMS.txt is present alongside downloaded CI installers, verify them BEFORE packaging
if (Test-Path $SourceSums) {
    Write-Host "==> Verifying input installers against source SHA256SUMS.txt..." -ForegroundColor Cyan
    $sourceLines = Get-Content -Path $SourceSums | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    foreach ($line in $sourceLines) {
        if ($line -match '^([0-9a-fA-F]{64})\s+\*?(.+)$') {
            $expectedHash = $Matches[1].ToLowerInvariant()
            $fileName = $Matches[2].Trim()
            $candidate = Join-Path $ResolvedInstallerDir $fileName
            if (Test-Path $candidate) {
                $actualHash = (Get-FileHash -Path $candidate -Algorithm SHA256).Hash.ToLowerInvariant()
                if ($actualHash -ne $expectedHash) {
                    throw "Input checksum mismatch for '$fileName': expected $expectedHash, actual $actualHash."
                }
                Write-Host "  [OK] Source checksum verified: $fileName ($actualHash)" -ForegroundColor Green
            }
        }
    }
}

foreach ($docFile in @("START_HERE.txt", "INSTALLATION_GUIDE.md", "VERSION.txt")) {
    $docPath = Join-Path $ReleaseDocsDir $docFile
    if (-not (Test-Path $docPath)) {
        throw "Required release documentation file missing: '$docPath'."
    }
}

$PackageFolderName = "ServerForge-Personal-Windows-x64-v$Version"
$StagingRoot = Join-Path $OutputDir "staging"
$PackageDir  = Join-Path $StagingRoot $PackageFolderName
$ZipPath     = Join-Path $OutputDir "$PackageFolderName.zip"
$ZipShaPath  = "$ZipPath.sha256"

if (Test-Path $StagingRoot) { Remove-Item -Recurse -Force $StagingRoot }
if (Test-Path $ZipPath)     { Remove-Item -Force $ZipPath }
if (Test-Path $ZipShaPath)  { Remove-Item -Force $ZipShaPath }

New-Item -ItemType Directory -Force -Path $PackageDir | Out-Null
New-Item -ItemType Directory -Force -Path $OutputDir  | Out-Null

Write-Host "==> Staging distribution folder: $PackageFolderName" -ForegroundColor Cyan
Copy-Item -Path $DesktopExeSrc -Destination (Join-Path $PackageDir "ServerForge-Desktop-Setup.exe") -Force
Copy-Item -Path $AgentExeSrc   -Destination (Join-Path $PackageDir "ServerForge-Agent-Setup.exe")   -Force
Copy-Item -Path (Join-Path $ReleaseDocsDir "START_HERE.txt")        -Destination (Join-Path $PackageDir "START_HERE.txt")        -Force
Copy-Item -Path (Join-Path $ReleaseDocsDir "INSTALLATION_GUIDE.md") -Destination (Join-Path $PackageDir "INSTALLATION_GUIDE.md") -Force

$desktopHash = (Get-FileHash -Path (Join-Path $PackageDir "ServerForge-Desktop-Setup.exe") -Algorithm SHA256).Hash.ToLowerInvariant()
$agentHash   = (Get-FileHash -Path (Join-Path $PackageDir "ServerForge-Agent-Setup.exe")   -Algorithm SHA256).Hash.ToLowerInvariant()
$desktopMb   = [Math]::Round((Get-Item (Join-Path $PackageDir "ServerForge-Desktop-Setup.exe")).Length / 1MB, 2)
$agentMb     = [Math]::Round((Get-Item (Join-Path $PackageDir "ServerForge-Agent-Setup.exe")).Length / 1MB, 2)
$packagedUtc = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")

$versionContent = (Get-Content -Path (Join-Path $ReleaseDocsDir "VERSION.txt") -Raw).TrimEnd()
$versionExtra = @"

PackagedAtUtc: $packagedUtc
InstallerSourceCommit: $SourceCommit
InstallerSourceCiRun: $SourceCiRunUrl
PackagedCommit: $(if ([string]::IsNullOrWhiteSpace($PackagedCommit)) { $SourceCommit } else { $PackagedCommit })
DesktopInstallerSizeMB: $desktopMb
DesktopInstallerSha256: $desktopHash
AgentInstallerSizeMB: $agentMb
AgentInstallerSha256: $agentHash
"@
Set-Content -Path (Join-Path $PackageDir "VERSION.txt") -Value ($versionContent + $versionExtra) -Encoding UTF8

# Generate complete SHA256SUMS.txt covering both installers + release docs
$filesToHash = @(
    "ServerForge-Desktop-Setup.exe",
    "ServerForge-Agent-Setup.exe",
    "START_HERE.txt",
    "INSTALLATION_GUIDE.md",
    "VERSION.txt"
)

$checksumLines = @()
$expectedMap = @{}
foreach ($relName in $filesToHash) {
    $full = Join-Path $PackageDir $relName
    $hash = (Get-FileHash -Path $full -Algorithm SHA256).Hash.ToLowerInvariant()
    $expectedMap[$relName] = $hash
    $checksumLines += "$hash  $relName"
}
$checksumsPath = Join-Path $PackageDir "SHA256SUMS.txt"
Set-Content -Path $checksumsPath -Value $checksumLines -Encoding UTF8

Write-Host "==> Creating distribution archive: $ZipPath" -ForegroundColor Cyan
# Include base directory so archive extracts cleanly into ServerForge-Personal-Windows-x64-v1.0.0/...
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $PackageDir,
    $ZipPath,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $true
)

if (-not (Test-Path $ZipPath) -or ((Get-Item $ZipPath).Length -le 0)) {
    throw "Failed to create non-empty ZIP archive at '$ZipPath'."
}

Write-Host "==> Verifying ZIP entries and byte-for-byte SHA-256 digests..." -ForegroundColor Cyan
$requiredEntries = @(
    "$PackageFolderName/ServerForge-Desktop-Setup.exe",
    "$PackageFolderName/ServerForge-Agent-Setup.exe",
    "$PackageFolderName/SHA256SUMS.txt",
    "$PackageFolderName/START_HERE.txt",
    "$PackageFolderName/INSTALLATION_GUIDE.md",
    "$PackageFolderName/VERSION.txt"
)

$archive = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
try {
    $entryLookup = @{}
    foreach ($entry in $archive.Entries) {
        $normalized = $entry.FullName.Replace('\', '/')
        $entryLookup[$normalized] = $entry
    }

    foreach ($req in $requiredEntries) {
        if (-not $entryLookup.ContainsKey($req)) {
            throw "Required ZIP entry missing: '$req'."
        }
        $e = $entryLookup[$req]
        if ($e.Length -le 0) {
            throw "Required ZIP entry is empty: '$req'."
        }

        $leaf = $req.Substring($PackageFolderName.Length + 1)
        if ($expectedMap.ContainsKey($leaf)) {
            $sha256 = [System.Security.Cryptography.SHA256]::Create()
            $es = $e.Open()
            try {
                $hashBytes = $sha256.ComputeHash($es)
                $actual = ([System.BitConverter]::ToString($hashBytes) -replace '-', '').ToLowerInvariant()
            }
            finally {
                $es.Dispose()
                $sha256.Dispose()
            }
            if ($actual -ne $expectedMap[$leaf]) {
                throw "ZIP entry SHA-256 verification failed for '$req': expected $($expectedMap[$leaf]), got $actual."
            }
            Write-Host "  [VERIFIED] $req ($($e.Length) bytes | sha256:$actual)" -ForegroundColor Green
        }
        else {
            Write-Host "  [VERIFIED] $req ($($e.Length) bytes)" -ForegroundColor Green
        }
    }
}
finally {
    $archive.Dispose()
}

$zipHash = (Get-FileHash -Path $ZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$zipSizeMb = [Math]::Round((Get-Item $ZipPath).Length / 1MB, 2)
Set-Content -Path $ZipShaPath -Value "$zipHash  $PackageFolderName.zip" -Encoding UTF8

# Also copy SHA256SUMS.txt to OutputDir so release workflows can attach it directly alongside the ZIP
Copy-Item -Path $checksumsPath -Destination (Join-Path $OutputDir "SHA256SUMS.txt") -Force

Write-Host ""
Write-Host "=== PERSONAL RELEASE ZIP VERIFIED ===" -ForegroundColor Green
Write-Host "Archive: $ZipPath ($zipSizeMb MB)"
Write-Host "SHA-256: $zipHash"
exit 0
