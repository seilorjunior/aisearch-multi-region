#Requires -Version 7.0
<#
.SYNOPSIS
    End-to-end test suite for the Application Gateway → Azure AI Search path.

.DESCRIPTION
    Validates:
      1. AppGW HTTPS reachability (TLS handshake + HTTP 200)
      2. Health-probe endpoint (/ping) on every regional backend, direct
      3. Search pass-through via AppGW  (query returns expected documents)
      4. Load distribution — fires N queries and confirms all succeed
      5. Per-region direct query for cross-check (document counts match)

    Auth: DefaultAzureCredential via 'az account get-access-token'.
    Runs standalone — does NOT require the dotnet project to be built.

.PARAMETER Queries
    Number of queries to fire through the gateway for the load test (default 20).

.PARAMETER SearchTerm
    Full-text search term to use for all gateway queries (default "*").

.PARAMETER SkipSslValidation
    Skip TLS certificate validation (required when the gateway uses a self-signed cert).

.EXAMPLE
    .\scripts\test-appgw.ps1
    .\scripts\test-appgw.ps1 -Queries 50 -SearchTerm "wireless"
#>

param(
    [int]    $Queries          = 20,
    [string] $SearchTerm       = "*",
    [switch] $SkipSslValidation
)

$ErrorActionPreference = "Stop"
$script:passed = 0
$script:failed = 0

# ── Helpers ──────────────────────────────────────────────────────────────────

function Write-Pass([string]$msg) {
    Write-Host "  [PASS] $msg" -ForegroundColor Green
    $script:passed++
}

function Write-Fail([string]$msg) {
    Write-Host "  [FAIL] $msg" -ForegroundColor Red
    $script:failed++
}

function Write-Section([string]$title) {
    Write-Host ""
    Write-Host "── $title ──" -ForegroundColor Cyan
}

function New-HttpClient {
    $handler = [System.Net.Http.HttpClientHandler]::new()
    if ($SkipSslValidation) {
        $handler.ServerCertificateCustomValidationCallback =
            [System.Net.Http.HttpClientHandler]::DangerousAcceptAnyServerCertificateValidator
    }
    [System.Net.Http.HttpClient]::new($handler)
}

function Get-SearchToken {
    $raw = az account get-access-token --resource "https://search.azure.com" -o json 2>&1
    if ($LASTEXITCODE -ne 0) { throw "az account get-access-token failed: $raw" }
    ($raw | ConvertFrom-Json).accessToken
}

function Invoke-SearchQuery {
    param(
        [System.Net.Http.HttpClient] $Client,
        [string] $BaseUrl,
        [string] $IndexName,
        [string] $Token,
        [string] $Term = "*",
        [int]    $Top  = 5
    )
    $apiVersion = "2024-05-01-preview"
    $body = @{ search = $Term; top = $Top; count = $true } | ConvertTo-Json
    $content = [System.Net.Http.StringContent]::new(
        $body,
        [System.Text.Encoding]::UTF8,
        "application/json"
    )

    $url = "$BaseUrl/indexes/$IndexName/docs/search?api-version=$apiVersion"
    $request = [System.Net.Http.HttpRequestMessage]::new(
        [System.Net.Http.HttpMethod]::Post, $url
    )
    $request.Content = $content
    $request.Headers.Authorization =
        [System.Net.Http.Headers.AuthenticationHeaderValue]::new("Bearer", $Token)

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $response = $Client.SendAsync($request).GetAwaiter().GetResult()
    $sw.Stop()
    $responseBody = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    return @{
        StatusCode   = [int]$response.StatusCode
        Body         = $responseBody
        ElapsedMs    = $sw.ElapsedMilliseconds
        IsSuccess    = $response.IsSuccessStatusCode
    }
}

# ── Load config ───────────────────────────────────────────────────────────────

$repoRoot      = Split-Path $PSScriptRoot -Parent
$settingsPath  = Join-Path $repoRoot "src" "MultiRegionSearch" "appsettings.json"

if (-not (Test-Path $settingsPath)) {
    throw "appsettings.json not found at '$settingsPath'. Run postprovision.ps1 first."
}

$settings   = Get-Content $settingsPath -Raw | ConvertFrom-Json
$gatewayUrl = $settings.Search.Gateway.Url
$indexName  = $settings.Search.IndexName
$regions    = $settings.Search.Regions

if ($settings.Search.Gateway.AllowSelfSignedCert -and -not $SkipSslValidation) {
    Write-Host "INFO: appsettings.json has AllowSelfSignedCert=true — enabling -SkipSslValidation automatically." -ForegroundColor Yellow
    $SkipSslValidation = $true
}

Write-Host ""
Write-Host "AppGW Test Suite" -ForegroundColor White
Write-Host "  Gateway  : $gatewayUrl"
Write-Host "  Index    : $indexName"
Write-Host "  Regions  : $($regions | ForEach-Object { $_.Name } | Join-String -Separator ', ')"
Write-Host "  Queries  : $Queries"

# ── Acquire token ─────────────────────────────────────────────────────────────

Write-Section "Auth"
try {
    $token = Get-SearchToken
    Write-Pass "Acquired Azure AD token for https://search.azure.com"
} catch {
    Write-Fail "Could not acquire token: $_"
    Write-Host "Run 'az login' and retry." -ForegroundColor Yellow
    exit 1
}

$client = New-HttpClient

# ── 1. AppGW HTTPS reachability ───────────────────────────────────────────────

Write-Section "1. AppGW Reachability"

try {
    $result = Invoke-SearchQuery -Client $client -BaseUrl $gatewayUrl `
                                 -IndexName $indexName -Token $token -Term $SearchTerm -Top 1
    if ($result.IsSuccess) {
        $count = ($result.Body | ConvertFrom-Json).'@odata.count'
        Write-Pass "AppGW responded HTTP $($result.StatusCode) in $($result.ElapsedMs) ms  (count=$count)"
    } else {
        Write-Fail "AppGW returned HTTP $($result.StatusCode): $($result.Body)"
    }
} catch {
    Write-Fail "AppGW request threw exception: $_"
}

# ── 2. Health probe on each backend ──────────────────────────────────────────

Write-Section "2. Backend Health Probes (/ping)"

foreach ($region in $regions) {
    $pingUrl = "$($region.Endpoint)/ping"
    try {
        # /ping is unauthenticated — use a bare GET
        $req = [System.Net.Http.HttpRequestMessage]::new(
            [System.Net.Http.HttpMethod]::Get, $pingUrl
        )
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $resp = $client.SendAsync($req).GetAwaiter().GetResult()
        $sw.Stop()
        if ([int]$resp.StatusCode -eq 200) {
            Write-Pass "$($region.Name): /ping → 200 OK  ($($sw.ElapsedMilliseconds) ms)"
        } else {
            Write-Fail "$($region.Name): /ping → $([int]$resp.StatusCode)"
        }
    } catch {
        Write-Fail "$($region.Name): /ping threw exception: $_"
    }
}

# ── 3. Search pass-through via AppGW ─────────────────────────────────────────

Write-Section "3. Search Pass-Through via AppGW"

$terms = @("wireless", "coffee", "laptop", "chair", "bottle", "*")

foreach ($term in $terms) {
    try {
        $result = Invoke-SearchQuery -Client $client -BaseUrl $gatewayUrl `
                                     -IndexName $indexName -Token $token -Term $term -Top 5
        if ($result.IsSuccess) {
            $parsed = $result.Body | ConvertFrom-Json
            $count  = $parsed.'@odata.count'
            $hits   = $parsed.value.Count
            Write-Pass "term='$term'  total=$count  returned=$hits  ($($result.ElapsedMs) ms)"
        } else {
            Write-Fail "term='$term' → HTTP $($result.StatusCode): $($result.Body)"
        }
    } catch {
        Write-Fail "term='$term' threw exception: $_"
    }
}

# ── 4. Load test (N queries, all must succeed) ────────────────────────────────

Write-Section "4. Load Test ($Queries queries via AppGW)"

$terms4 = @("wireless", "coffee", "laptop", "chair", "bottle", "*")
$ok      = 0
$fail4   = 0
$latencies = [System.Collections.Generic.List[long]]::new()

for ($i = 0; $i -lt $Queries; $i++) {
    $term = $terms4[$i % $terms4.Length]
    try {
        $result = Invoke-SearchQuery -Client $client -BaseUrl $gatewayUrl `
                                     -IndexName $indexName -Token $token -Term $term -Top 1
        if ($result.IsSuccess) {
            $ok++
            $latencies.Add($result.ElapsedMs)
        } else {
            $fail4++
            Write-Host "   request $i ($term) → HTTP $($result.StatusCode)" -ForegroundColor DarkYellow
        }
    } catch {
        $fail4++
        Write-Host "   request $i ($term) threw exception: $_" -ForegroundColor DarkYellow
    }
}

if ($fail4 -eq 0) {
    $sorted = $latencies | Sort-Object
    $p50 = $sorted[[int]($sorted.Count * 0.50)]
    $p95 = $sorted[[int]($sorted.Count * 0.95)]
    Write-Pass "$ok/$Queries succeeded  |  min=$($sorted[0]) ms  p50=$p50 ms  p95=$p95 ms  max=$($sorted[-1]) ms"
} else {
    Write-Fail "$fail4/$Queries requests failed  ($ok succeeded)"
}

# ── 5. Per-region direct query — document count parity ───────────────────────

Write-Section "5. Per-Region Direct Query (count parity)"

$counts = @{}

foreach ($region in $regions) {
    try {
        $result = Invoke-SearchQuery -Client $client -BaseUrl $region.Endpoint `
                                     -IndexName $indexName -Token $token -Term "*" -Top 0
        if ($result.IsSuccess) {
            $count = ($result.Body | ConvertFrom-Json).'@odata.count'
            $counts[$region.Name] = $count
            Write-Host "  $($region.Name): $count documents  ($($result.ElapsedMs) ms)"
        } else {
            Write-Fail "$($region.Name): HTTP $($result.StatusCode)"
            $counts[$region.Name] = -1
        }
    } catch {
        Write-Fail "$($region.Name): exception — $_"
        $counts[$region.Name] = -1
    }
}

$distinctCounts = $counts.Values | Sort-Object -Unique
if ($distinctCounts.Count -eq 1 -and $distinctCounts[0] -ge 0) {
    Write-Pass "All regions report identical document count ($($distinctCounts[0]))"
} else {
    Write-Fail "Document counts differ across regions: $($counts | ConvertTo-Json -Compress)"
}

# ── Summary ───────────────────────────────────────────────────────────────────

Write-Host ""
Write-Host "══════════════════════════════════════════" -ForegroundColor White
$total = $script:passed + $script:failed
Write-Host "Results: $($script:passed)/$total passed" -ForegroundColor $(if ($script:failed -eq 0) { "Green" } else { "Red" })
Write-Host "══════════════════════════════════════════" -ForegroundColor White
Write-Host ""

if ($script:failed -gt 0) { exit 1 }
