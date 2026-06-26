#Requires -Version 7.0
<#
.SYNOPSIS
    azd post-provision hook — writes src/MultiRegionSearch/appsettings.json
    from the Bicep deployment outputs exposed as azd environment variables.

.NOTES
    azd maps Bicep string outputs to env vars automatically (camelCase → uppercase
    with underscores).  The 'searchEndpoints' array output is serialised as JSON.
    Falls back to 'az deployment group list' if the env vars are not present.
#>

param()
$ErrorActionPreference = "Stop"

$scriptDir = $PSScriptRoot   # scripts/
$repoRoot  = Split-Path $scriptDir -Parent

# ── 1. Resolve output values ──────────────────────────────────────────────────
# azd uppercases the Bicep output name as-is (no camelCase→snake conversion),
# so 'gatewayUrl' → 'GATEWAYURL'. Try both styles for robustness.
$gatewayUrl         = $env:GATEWAYURL        ?? $env:GATEWAY_URL
$indexName          = $env:INDEXNAME         ?? $env:INDEX_NAME
$searchEndpointsRaw = $env:SEARCHENDPOINTS   ?? $env:SEARCH_ENDPOINTS

if (-not $gatewayUrl) {
    Write-Host "GATEWAY_URL not in azd env — falling back to 'az deployment group list'."

    $rg = $env:AZURE_RESOURCE_GROUP
    if (-not $rg) { throw "AZURE_RESOURCE_GROUP is not set." }

    $deploymentsJson = az deployment group list -g $rg --query "[?properties.provisioningState=='Succeeded']" -o json
    $deployments = $deploymentsJson | ConvertFrom-Json
    if ($deployments.Count -eq 0) { throw "No successful deployments found in resource group '$rg'." }

    # Pick the most recent deployment that has gatewayUrl output
    $dep = $deployments |
        Where-Object { $_.properties.outputs.PSObject.Properties.Name -contains 'gatewayUrl' } |
        Sort-Object { $_.properties.timestamp } |
        Select-Object -Last 1

    if (-not $dep) { throw "Could not find a deployment with 'gatewayUrl' output in '$rg'." }

    $outputs           = $dep.properties.outputs
    $gatewayUrl        = $outputs.gatewayUrl.value
    $indexName         = $outputs.indexName.value
    $searchEndpointsRaw = $outputs.searchEndpoints.value | ConvertTo-Json -Compress
}

$indexName = $indexName ?? "products"

# ── 2. Parse the search endpoints array ──────────────────────────────────────
$regions = @()
if ($searchEndpointsRaw) {
    $endpoints = $searchEndpointsRaw | ConvertFrom-Json
    foreach ($e in $endpoints) {
        $regions += [ordered]@{
            Name     = $e.region
            Endpoint = $e.endpoint
        }
    }
}

if ($regions.Count -eq 0) {
    Write-Warning "No search endpoints found in outputs; appsettings.json 'Regions' will be empty."
}

# ── 3. Build and write appsettings.json ───────────────────────────────────────
$appsettings = [ordered]@{
    Search = [ordered]@{
        IndexName = $indexName
        Gateway   = [ordered]@{
            Url                = $gatewayUrl
            AllowSelfSignedCert = $true
        }
        Regions = $regions
    }
}

$appsettingsPath = Join-Path $repoRoot "src" "MultiRegionSearch" "appsettings.json"
$appsettings | ConvertTo-Json -Depth 6 | Set-Content -Path $appsettingsPath -Encoding utf8

Write-Host ""
Write-Host "appsettings.json written to: $appsettingsPath"
Write-Host "  Gateway URL : $gatewayUrl"
Write-Host "  Index name  : $indexName"
Write-Host "  Regions     : $(($regions | ForEach-Object { $_.Name }) -join ', ')"
Write-Host ""
Write-Host "RBAC propagation can take 1-2 minutes. Then run:"
Write-Host "  cd src/MultiRegionSearch"
Write-Host "  dotnet run -- demo"
