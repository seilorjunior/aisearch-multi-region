#Requires -Version 7.0
<#
.SYNOPSIS
    azd pre-provision hook — generates a self-signed TLS certificate for the
    Application Gateway HTTPS listener and stores all required values in the
    azd environment so main.parameters.json can reference them.

.NOTES
    Runs automatically before 'azd provision'.  Skips cert regeneration if
    SSL_CERT_DATA is already present in the azd environment (idempotent re-runs).
#>

param()
$ErrorActionPreference = "Stop"

if ($env:ALLOW_SELF_SIGNED_CERT -and $env:ALLOW_SELF_SIGNED_CERT -notin @("true", "false")) {
    throw "ALLOW_SELF_SIGNED_CERT must be true or false."
}
if ($env:GATEWAY_HOST_NAME -and [Uri]::CheckHostName($env:GATEWAY_HOST_NAME) -ne [UriHostNameType]::Dns) {
    throw "GATEWAY_HOST_NAME must be a DNS hostname, not a URL."
}
if (-not $env:GATEWAY_HOST_NAME) {
    azd env set GATEWAY_HOST_NAME ""
    if ($LASTEXITCODE -ne 0) { throw "Could not set GATEWAY_HOST_NAME." }
}

# ── 1. Derive stable NAME_PREFIX / DNS_LABEL from the azd env name ──────────
$envName = $env:AZURE_ENV_NAME
if (-not $envName) { throw "AZURE_ENV_NAME is not set. Run 'azd env select' or 'azd env new'." }

# Sanitise: lowercase letters and digits only, then cap at 20 chars so that
# adding a "-<region>" suffix keeps search service names under 30 chars.
$sanitised = ($envName.ToLower() -replace '[^a-z0-9]', '')
if ($sanitised.Length -eq 0) { $sanitised = "aismr" }
$namePrefix = "aismr" + $sanitised.Substring(0, [Math]::Min(15, $sanitised.Length))
$dnsLabel   = $namePrefix   # DNS label for the gateway public IP

azd env set NAME_PREFIX $namePrefix
azd env set DNS_LABEL   $dnsLabel
Write-Host "NAME_PREFIX = $namePrefix"

# ── 2. Default AZURE_PRINCIPAL_TYPE if not already set ───────────────────────
if (-not $env:AZURE_PRINCIPAL_TYPE) {
    azd env set AZURE_PRINCIPAL_TYPE User
    Write-Host "AZURE_PRINCIPAL_TYPE defaulted to 'User'"
}

# ── 3. Generate self-signed cert (skip if already present) ───────────────────
if ($env:SSL_CERT_DATA) {
    if (-not $env:SSL_CERT_PASSWORD) { throw "SSL_CERT_PASSWORD is required when SSL_CERT_DATA is set." }
    Write-Host "SSL_CERT_DATA already set — skipping cert generation."
    exit 0
}

$location = $env:AZURE_LOCATION
if (-not $location) { throw "AZURE_LOCATION is not set. Run 'azd env set AZURE_LOCATION <region>'." }

$fqdn = if ($env:GATEWAY_HOST_NAME) { $env:GATEWAY_HOST_NAME } else { "$dnsLabel.$location.cloudapp.azure.com" }
Write-Host "Generating self-signed certificate for: $fqdn"

$certPassword = [System.Guid]::NewGuid().ToString("N")
$pfxPath = Join-Path ([System.IO.Path]::GetTempPath()) "appgw-$dnsLabel.pfx"

if ($IsWindows) {
    $cert = New-SelfSignedCertificate `
        -DnsName $fqdn `
        -CertStoreLocation "Cert:\CurrentUser\My" `
        -KeyExportPolicy Exportable `
        -KeyLength 2048 `
        -NotAfter (Get-Date).AddYears(2)

    $securePwd = ConvertTo-SecureString -String $certPassword -Force -AsPlainText
    Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $securePwd | Out-Null
    Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)" -Force -ErrorAction SilentlyContinue
} else {
    # Linux / macOS — use openssl (available on all standard distros and macOS)
    $keyPath = Join-Path ([System.IO.Path]::GetTempPath()) "appgw-$dnsLabel.key"
    $crtPath = Join-Path ([System.IO.Path]::GetTempPath()) "appgw-$dnsLabel.crt"
    try {
        openssl req -x509 -nodes -newkey rsa:2048 `
            -keyout $keyPath -out $crtPath `
            -days 730 -subj "/CN=$fqdn" -addext "subjectAltName=DNS:$fqdn" 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "OpenSSL certificate generation failed." }
        openssl pkcs12 -export `
            -out $pfxPath -inkey $keyPath -in $crtPath `
            -passout "pass:$certPassword" 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "OpenSSL PFX export failed." }
    } finally {
        Remove-Item $keyPath, $crtPath -Force -ErrorAction SilentlyContinue
    }
}

$certData = [System.Convert]::ToBase64String([System.IO.File]::ReadAllBytes($pfxPath))
Remove-Item $pfxPath -Force

azd env set SSL_CERT_DATA     $certData
azd env set SSL_CERT_PASSWORD $certPassword

Write-Host "Certificate generated and stored in azd environment."
Write-Host "Gateway FQDN will be: $fqdn"
