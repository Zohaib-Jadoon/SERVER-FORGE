Set-StrictMode -Version Latest

function Resolve-E2EAbsolutePath {
    param([Parameter(Mandatory = $true)][string]$PathValue)
    if ([System.IO.Path]::IsPathRooted($PathValue)) {
        return [System.IO.Path]::GetFullPath($PathValue)
    }
    return [System.IO.Path]::GetFullPath((Join-Path (Get-Location).Path $PathValue))
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Assert-OwnedE2EName {
    param(
        [Parameter(Mandatory = $true)][string]$ResourceName,
        [Parameter(Mandatory = $true)][string]$ExpectedPrefix
    )
    if ([string]::IsNullOrWhiteSpace($ResourceName) -or
        -not $ResourceName.StartsWith('ServerForge_E2E_', [StringComparison]::OrdinalIgnoreCase) -or
        -not $ResourceName.StartsWith($ExpectedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "SAFETY ABORT: Resource '$ResourceName' does not match disposable ownership prefix '$ExpectedPrefix'. Refusing mutation."
    }
}

function Protect-SanitizedText {
    param(
        [string]$Text,
        [string[]]$ExtraSecrets = @()
    )
    if ([string]::IsNullOrEmpty($Text)) { return '' }
    $clean = $Text
    $clean = [regex]::Replace($clean, 'github_pat_[A-Za-z0-9_]+', '[REDACTED_GITHUB_PAT]')
    $clean = [regex]::Replace($clean, 'ghp_[A-Za-z0-9]+', '[REDACTED_GITHUB_PAT]')
    $clean = [regex]::Replace($clean, 'gho_[A-Za-z0-9]+', '[REDACTED_GITHUB_TOKEN]')
    $clean = [regex]::Replace($clean, '(?i)(Password|Pwd|Secret|Token|ApiKey)\s*=\s*[^;\r\n\s"]+', '$1=[REDACTED]')
    $clean = [regex]::Replace($clean, '(?i)(Pairing\s*Code\s*:\s*)[A-Z0-9\-]+', '$1[REDACTED_PAIRING_CODE]')
    foreach ($sec in $ExtraSecrets) {
        if (-not [string]::IsNullOrWhiteSpace($sec) -and $sec.Length -ge 6) {
            $clean = $clean.Replace($sec, '[REDACTED]')
        }
    }
    return $clean
}

function Write-E2ELog {
    param(
        [Parameter(Mandatory = $true)][string]$Message,
        [string]$Level = 'INFO',
        [string]$LogFile = ''
    )
    $safeMsg = Protect-SanitizedText -Text $Message
    $line = "[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff')] [$Level] $safeMsg"
    Write-Host $line
    if (-not [string]::IsNullOrWhiteSpace($LogFile)) {
        $parent = Split-Path -Path $LogFile -Parent
        if (-not [string]::IsNullOrWhiteSpace($parent) -and -not (Test-Path $parent)) {
            New-Item -ItemType Directory -Path $parent -Force | Out-Null
        }
        Add-Content -Path $LogFile -Value $line -Encoding UTF8
    }
}

function Wait-E2ECondition {
    param(
        [Parameter(Mandatory = $true)][scriptblock]$Condition,
        [int]$TimeoutSeconds = 30,
        [int]$PollMilliseconds = 500
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            if (& $Condition) { return $true }
        } catch { }
        Start-Sleep -Milliseconds $PollMilliseconds
    }
    try { return [bool](& $Condition) } catch { return $false }
}

function Get-IisSitesSnapshot {
    $appCmd = Join-Path $env:SystemRoot 'System32\inetsrv\appcmd.exe'
    if (-not (Test-Path $appCmd)) { return @() }
    $raw = & $appCmd list site /xml 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace(($raw -join ''))) { return @() }
    [xml]$xml = $raw -join "`n"
    $sites = @()
    foreach ($s in @($xml.appcmd.SITE)) {
        if ($null -eq $s) { continue }
        $sites += [ordered]@{
            Name     = [string]$s.'SITE.NAME'
            Id       = [string]$s.'SITE.ID'
            Bindings = [string]$s.bindings
            State    = [string]$s.state
        }
    }
    return $sites
}

function Get-IisAppPoolsSnapshot {
    $appCmd = Join-Path $env:SystemRoot 'System32\inetsrv\appcmd.exe'
    if (-not (Test-Path $appCmd)) { return @() }
    $raw = & $appCmd list apppool /xml 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace(($raw -join ''))) { return @() }
    [xml]$xml = $raw -join "`n"
    $pools = @()
    foreach ($p in @($xml.appcmd.APPPOOL)) {
        if ($null -eq $p) { continue }
        $pools += [ordered]@{
            Name           = [string]$p.'APPPOOL.NAME'
            RuntimeVersion = [string]$p.RuntimeVersion
            PipelineMode   = [string]$p.PipelineMode
            State          = [string]$p.state
        }
    }
    return $pools
}

function Test-TcpPortInUse {
    param([Parameter(Mandatory = $true)][int]$Port)
    $listeners = [System.Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners()
    foreach ($ep in $listeners) {
        if ($ep.Port -eq $Port) { return $true }
    }
    return $false
}

function New-E2EDesktopClientCertificate {
    param(
        [Parameter(Mandatory = $true)][string]$PfxPath,
        [string]$CommonName = 'ServerForgeE2EDesktop'
    )
    $parent = Split-Path -Path $PfxPath -Parent
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    $flags = [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::UserKeySet -bor
             [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::PersistKeySet -bor
             [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::Exportable

    if (Test-Path $PfxPath) {
        return New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($PfxPath, $null, $flags)
    }

    $ecdsa = [System.Security.Cryptography.ECDsa]::Create([System.Security.Cryptography.ECCurve+NamedCurves]::nistP384)
    try {
        $req = New-Object System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=$CommonName",
            $ecdsa,
            [System.Security.Cryptography.HashAlgorithmName]::SHA384)
        $oidCol = New-Object System.Security.Cryptography.OidCollection
        [void]$oidCol.Add((New-Object System.Security.Cryptography.Oid('1.3.6.1.5.5.7.3.2'))) # Client Authentication
        $req.CertificateExtensions.Add((New-Object System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension($oidCol, $false)))
        $notBefore = [DateTimeOffset]::UtcNow.AddMinutes(-5)
        $notAfter = [DateTimeOffset]::UtcNow.AddDays(7)
        $ephemeral = $req.CreateSelfSigned($notBefore, $notAfter)
        try {
            $pfxBytes = $ephemeral.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx)
            [System.IO.File]::WriteAllBytes($PfxPath, $pfxBytes)
        }
        finally {
            $ephemeral.Dispose()
        }
    }
    finally {
        $ecdsa.Dispose()
    }

    return New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($PfxPath, $null, $flags)
}

function ConvertTo-CertPem {
    param([Parameter(Mandatory = $true)][System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate)
    $raw = [Convert]::ToBase64String($Certificate.RawData, [Base64FormattingOptions]::InsertLineBreaks)
    return "-----BEGIN CERTIFICATE-----`n$raw`n-----END CERTIFICATE-----"
}

function Invoke-PinnedAgentRequest {
    param(
        [Parameter(Mandatory = $true)][string]$BaseUrl,
        [Parameter(Mandatory = $true)][string]$Path,
        [string]$Method = 'GET',
        [Parameter(Mandatory = $true)][string]$ExpectedServerThumbprint,
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$ClientCertificate = $null,
        [object]$Body = $null,
        [int]$TimeoutSeconds = 60
    )

    Add-Type -AssemblyName System.Net.Http -ErrorAction SilentlyContinue
    $normalizedExpected = ($ExpectedServerThumbprint -replace '[:\s-]', '').ToUpperInvariant()

    $handler = New-Object System.Net.Http.HttpClientHandler
    $handler.ClientCertificateOptions = [System.Net.Http.ClientCertificateOption]::Manual
    if ($null -ne $ClientCertificate) {
        [void]$handler.ClientCertificates.Add($ClientCertificate)
    }

    # Strict fail-closed server certificate thumbprint pinning — NEVER disables TLS validation
    $handler.ServerCertificateCustomValidationCallback = {
        param($msg, $cert, $chain, $errors)
        if ($null -eq $cert) { return $false }
        $actual = ($cert.Thumbprint -replace '[:\s-]', '').ToUpperInvariant()
        return [string]::Equals($actual, $normalizedExpected, [StringComparison]::OrdinalIgnoreCase)
    }.GetNewClosure()

    $http = New-Object System.Net.Http.HttpClient($handler)
    $http.BaseAddress = New-Object Uri($BaseUrl.TrimEnd('/') + '/')
    $http.Timeout = [TimeSpan]::FromSeconds($TimeoutSeconds)

    try {
        $reqMsg = New-Object System.Net.Http.HttpRequestMessage((New-Object System.Net.Http.HttpMethod($Method)), $Path.TrimStart('/'))
        if ($null -ne $Body) {
            $json = if ($Body -is [string]) { $Body } else { $Body | ConvertTo-Json -Depth 10 -Compress }
            $reqMsg.Content = New-Object System.Net.Http.StringContent($json, [System.Text.Encoding]::UTF8, 'application/json')
        }
        $resp = $http.SendAsync($reqMsg).GetAwaiter().GetResult()
        $respBody = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        $parsed = $null
        if (-not [string]::IsNullOrWhiteSpace($respBody)) {
            try { $parsed = $respBody | ConvertFrom-Json } catch { $parsed = $null }
        }
        return [PSCustomObject]@{
            StatusCode = [int]$resp.StatusCode
            IsSuccess  = [bool]$resp.IsSuccessStatusCode
            RawBody    = $respBody
            Json       = $parsed
        }
    }
    finally {
        $http.Dispose()
        $handler.Dispose()
    }
}

Export-ModuleMember -Function *
