#Requires -Version 7.0
<#
.SYNOPSIS
    Provisions the multi-region Azure AI Search + Application Gateway demo and wires up the C# app.

.DESCRIPTION
    1. Resolves the signed-in principal (gets data-plane RBAC on each search service).
    2. Generates a self-signed certificate for the gateway HTTPS listener.
    3. Deploys infra/main.bicep (search services in each region + Application Gateway).
    4. Writes src/MultiRegionSearch/appsettings.json from the deployment outputs.

.NOTES
    Requires the Azure CLI, .NET 8 SDK, and Owner / User Access Administrator on the
    target resource group (so the Bicep role assignments can be created).
#>
[CmdletBinding()]
param(
    [string]$ResourceGroup = "rg-aisearch-multiregion",
    [string]$Location = "eastus2",
    [string[]]$SearchRegions = @("eastus2", "westus2"),
    [string]$DnsLabel = "aismr$(Get-Random -Maximum 99999)",
    [string]$SubscriptionId
)

$ErrorActionPreference = "Stop"

if ($SubscriptionId) {
    az account set --subscription $SubscriptionId | Out-Null
}

Write-Host "==> Resolving signed-in principal..." -ForegroundColor Cyan
$principalId = az ad signed-in-user show --query id -o tsv
if (-not $principalId) { throw "Could not resolve signed-in user. Run 'az login' first." }

$fqdn = "$DnsLabel.$Location.cloudapp.azure.com"
Write-Host "==> Generating self-signed certificate for $fqdn ..." -ForegroundColor Cyan
$certPassword = [System.Guid]::NewGuid().ToString("N")
$cert = New-SelfSignedCertificate `
    -DnsName $fqdn `
    -CertStoreLocation "Cert:\CurrentUser\My" `
    -KeyExportPolicy Exportable `
    -KeyLength 2048 `
    -NotAfter (Get-Date).AddYears(2)
$securePwd = ConvertTo-SecureString -String $certPassword -Force -AsPlainText
$pfxPath = Join-Path $env:TEMP "appgw-$DnsLabel.pfx"
Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $securePwd | Out-Null
$certData = [System.Convert]::ToBase64String([System.IO.File]::ReadAllBytes($pfxPath))
Remove-Item $pfxPath -Force
# Tidy up the cert store entry; the PFX bytes are already deployed.
Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)" -Force -ErrorAction SilentlyContinue

Write-Host "==> Creating resource group $ResourceGroup in $Location ..." -ForegroundColor Cyan
az group create -n $ResourceGroup -l $Location | Out-Null

# ConvertTo-Json collapses a single-element array to a scalar, so build the JSON explicitly.
$regionsJson = "[" + (($SearchRegions | ForEach-Object { "`"$_`"" }) -join ",") + "]"

Write-Host "==> Deploying infrastructure (Application Gateway provisioning takes ~6-8 minutes)..." -ForegroundColor Cyan
$outputsJson = az deployment group create `
    -g $ResourceGroup `
    -f "$PSScriptRoot/infra/main.bicep" `
    -p principalId=$principalId `
    -p searchRegions=$regionsJson `
    -p gatewayLocation=$Location `
    -p dnsLabel=$DnsLabel `
    -p sslCertData=$certData `
    -p sslCertPassword=$certPassword `
    --query properties.outputs -o json
if ($LASTEXITCODE -ne 0) { throw "Deployment failed." }

$outputs = $outputsJson | ConvertFrom-Json
$gatewayUrl = $outputs.gatewayUrl.value
$indexName = $outputs.indexName.value
$searchEndpoints = $outputs.searchEndpoints.value

Write-Host "==> Writing appsettings.json ..." -ForegroundColor Cyan
$regions = @()
foreach ($e in $searchEndpoints) {
    $regions += [ordered]@{ Name = $e.region; Endpoint = $e.endpoint }
}
$appsettings = [ordered]@{
    Search = [ordered]@{
        IndexName = $indexName
        Gateway   = [ordered]@{ Url = $gatewayUrl; AllowSelfSignedCert = $true }
        Regions   = $regions
    }
}
$appsettingsPath = "$PSScriptRoot/src/MultiRegionSearch/appsettings.json"
$appsettings | ConvertTo-Json -Depth 6 | Set-Content -Path $appsettingsPath -Encoding utf8

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "  Gateway URL : $gatewayUrl"
Write-Host "  Regions     : $(( $searchEndpoints | ForEach-Object { $_.region } ) -join ', ')"
Write-Host ""
Write-Host "RBAC role assignments can take 1-2 minutes to propagate. Then run:" -ForegroundColor Yellow
Write-Host "  cd src/MultiRegionSearch"
Write-Host "  dotnet run -- demo"
