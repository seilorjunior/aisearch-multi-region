#Requires -Version 7.0
<#
.SYNOPSIS
    Destructive, opt-in failover exercise for a dedicated PUBLIC-endpoint demo.
.DESCRIPTION
    Requires an explicit appsettings-format ConfigPath, Azure resource IDs/names,
    and RG tags purpose=isolated-failover and environment-id=<EnvironmentMarker>.
    Validates every backend and the gateway before creating disposable indexes.
    Disables one Search service's public access, observes direct rejection and
    gateway backend health, measures query failures/staleness, then restores access.
    Replays the missed sentinel update explicitly; this is NOT automatic replication.
    Never run against production. Process termination/host failure can prevent finally
    from running: keep an independent operator ready to restore publicNetworkAccess.
.EXAMPLE
    ./scripts/test-failover.ps1 -ConfigPath ./test-settings.json `
      -SubscriptionId <guid> -ResourceGroup rg-isolated-demo `
      -ApplicationGatewayName agw-isolated-demo -TargetRegion eastus `
      -EnvironmentMarker my-disposable-demo -AcknowledgeDestructiveTest
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $ConfigPath,
    [Parameter(Mandatory)][guid] $SubscriptionId,
    [Parameter(Mandatory)][string] $ResourceGroup,
    [Parameter(Mandatory)][string] $ApplicationGatewayName,
    [Parameter(Mandatory)][string] $TargetRegion,
    [Parameter(Mandatory)][string] $EnvironmentMarker,
    [switch] $AcknowledgeDestructiveTest,
    [switch] $SkipSslValidation,
    [ValidateRange(30, 600)][int] $TimeoutSeconds = 180,
    [ValidateRange(1, 30)][int] $PollIntervalSeconds = 5,
    [ValidateRange(1, 10)][int] $ConsecutiveSuccesses = 3
)

$ErrorActionPreference = 'Stop'
if (-not $AcknowledgeDestructiveTest) {
    throw 'No mutations performed. Supply -AcknowledgeDestructiveTest only for a disposable isolated environment.'
}
if ($ResourceGroup -notmatch '^[a-zA-Z0-9_.()-]+$' -or
    $ApplicationGatewayName -notmatch '^[a-zA-Z0-9_.-]+$' -or
    [string]::IsNullOrWhiteSpace($EnvironmentMarker)) {
    throw 'Explicit resource names and a nonempty environment marker are required.'
}

function Invoke-AzJson([string[]] $Arguments) {
    $start = [System.Diagnostics.ProcessStartInfo]::new('az')
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in ($Arguments + @('--only-show-errors', '--output', 'json'))) {
        $start.ArgumentList.Add($argument)
    }
    $process = [System.Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(60000)) {
            $process.Kill($true)
            throw "Azure CLI timed out after 60 seconds ($($Arguments[0]))."
        }
        if ($process.ExitCode -ne 0) { throw "Azure CLI failed: $($stderr.GetAwaiter().GetResult())" }
        $text = $stdout.GetAwaiter().GetResult()
        if (-not [string]::IsNullOrWhiteSpace($text)) { return $text | ConvertFrom-Json }
    } finally { $process.Dispose() }
}

function Invoke-Arm([string] $Method, [string] $Id, [string] $ApiVersion, $Body = $null) {
    $arguments = @('rest', '--method', $Method, '--url',
        "https://management.azure.com${Id}?api-version=$ApiVersion")
    if ($null -ne $Body) { $arguments += @('--body', ($Body | ConvertTo-Json -Depth 20 -Compress)) }
    Invoke-AzJson $arguments
}

function Get-BackendHealth {
    # The CLI follows the gateway's asynchronous backend-health operation.
    Invoke-AzJson @('network', 'application-gateway', 'show-backend-health',
        '--subscription', "$SubscriptionId", '--resource-group', $ResourceGroup,
        '--name', $ApplicationGatewayName)
}

function Assert-RootUrl([string] $Value) {
    $uri = $null
    if (-not [uri]::TryCreate($Value, [UriKind]::Absolute, [ref] $uri) -or
        $uri.Scheme -ne 'https' -or $uri.Port -ne 443 -or $uri.UserInfo -or
        $uri.AbsolutePath -ne '/' -or $uri.Query -or $uri.Fragment -or
        $Value -match '[<>\\?#]' -or $Value -match '(?i)replace|example|localhost') {
        throw "An explicit HTTPS root URL on port 443 is required: '$Value'."
    }
    return $uri
}

$settings = (Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json).Search
$regions = @($settings.Regions)
if ($regions.Count -lt 2) { throw 'At least two distinct regional Search services are required.' }
$gatewayUri = Assert-RootUrl $settings.Gateway.Url
$regionMap = @{}
$names = @{}
$rgId = "/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroup"
$rg = Invoke-Arm GET $rgId '2021-04-01'
if ($rg.tags.purpose -cne 'isolated-failover' -or $rg.tags.'environment-id' -cne $EnvironmentMarker) {
    throw 'Resource group lacks matching purpose=isolated-failover and environment-id safety tags. No mutations performed.'
}

foreach ($region in $regions) {
    $uri = Assert-RootUrl $region.Endpoint
    if ($uri.Host -notmatch '^([a-z0-9-]+)\.search\.windows\.net$' -or
        [string]::IsNullOrWhiteSpace($region.Name) -or $names.ContainsKey($region.Name) -or
        $regionMap.ContainsKey($uri.Host)) { throw 'Each region needs a unique name and public Azure Search endpoint.' }
    $serviceId = "$rgId/providers/Microsoft.Search/searchServices/$($Matches[1])"
    $service = Invoke-Arm GET $serviceId '2023-11-01'
    if ($service.id -ine $serviceId -or $service.properties.publicNetworkAccess -cne 'enabled' -and
        $service.properties.publicNetworkAccess -cne 'Enabled') { throw "Backend must belong to the dedicated RG and have public access enabled: $serviceId" }
    if (@($service.properties.privateEndpointConnections).Where({ $null -ne $_ }).Count -gt 0) {
        throw "Private endpoint topology is unsupported: disabling public access would not isolate gateway traffic ($serviceId)."
    }
    $regionMap[$uri.Host] = @{ Name = $region.Name; Endpoint = $uri.GetLeftPart([UriPartial]::Authority); Id = $serviceId }
    $names[$region.Name] = $uri.Host
}
if (-not $names.ContainsKey($TargetRegion)) { throw 'TargetRegion must match a configured region.' }
$target = $regionMap[$names[$TargetRegion]]
$gatewayId = "$rgId/providers/Microsoft.Network/applicationGateways/$ApplicationGatewayName"
$gateway = Invoke-Arm GET $gatewayId '2024-05-01'
if ($gateway.id -ine $gatewayId) { throw 'Gateway must belong to the dedicated resource group.' }
if (@($gateway.properties.backendAddressPools).Count -ne 1 -or
    @($gateway.properties.requestRoutingRules).Count -ne 1) {
    throw 'Only the simple demo topology (one backend pool and one routing rule) is supported.'
}
$rule = $gateway.properties.requestRoutingRules[0]
if ($rule.properties.ruleType -ine 'Basic' -or
    $rule.properties.backendAddressPool.id -ine $gateway.properties.backendAddressPools[0].id) {
    throw 'The demo gateway rule must route directly to the shared regional backend pool.'
}
$listener = @($gateway.properties.httpListeners |
    Where-Object { $_.id -ieq $rule.properties.httpListener.id })
if ($listener.Count -ne 1 -or $listener[0].properties.protocol -ine 'Https') {
    throw 'The demo rule must use an HTTPS listener.'
}
$listenerFrontend = @($gateway.properties.frontendIPConfigurations |
    Where-Object { $_.id -ieq $listener[0].properties.frontendIPConfiguration.id })
$listenerPort = @($gateway.properties.frontendPorts |
    Where-Object { $_.id -ieq $listener[0].properties.frontendPort.id })
if ($listenerFrontend.Count -ne 1 -or -not $listenerFrontend[0].properties.publicIPAddress.id -or
    $listenerPort.Count -ne 1 -or $listenerPort[0].properties.port -ne 443) {
    throw 'The tested HTTPS listener must use a public frontend on port 443.'
}
$backendHosts = @($gateway.properties.backendAddressPools | ForEach-Object {
    $_.properties.backendAddresses | ForEach-Object {
        if ($_.ipAddress -or -not $regionMap.ContainsKey([string]$_.fqdn)) {
            throw 'All gateway backends must be the explicitly configured public Search FQDNs; IP/private/unrelated backends are prohibited.'
        }
        $_.fqdn
    }
} | Sort-Object -Unique)
if ($backendHosts.Count -ne $regionMap.Count) { throw 'Every configured region must be a backend of this gateway.' }
$publicIps = @($listenerFrontend | ForEach-Object {
    if ($_.properties.publicIPAddress.id) {
        (Invoke-Arm GET $_.properties.publicIPAddress.id '2024-05-01').properties.ipAddress
    }
})
$resolved = @([System.Net.Dns]::GetHostAddresses($gatewayUri.Host) | ForEach-Object { $_.IPAddressToString })
if ($resolved.Count -eq 0 -or @($resolved | Where-Object { $_ -notin $publicIps }).Count -gt 0) {
    throw 'Configured gateway URL must resolve only to this dedicated gateway public IP.'
}

$token = (Invoke-AzJson @('account', 'get-access-token', '--subscription', "$SubscriptionId",
    '--resource', 'https://search.azure.com')).accessToken
if (-not $token) { throw 'No Search bearer token obtained.' }
$handler = [System.Net.Http.HttpClientHandler]::new()
if ($SkipSslValidation) {
    $handler.ServerCertificateCustomValidationCallback = [System.Net.Http.HttpClientHandler]::DangerousAcceptAnyServerCertificateValidator
}
$client = [System.Net.Http.HttpClient]::new($handler)
$client.Timeout = [TimeSpan]::FromSeconds(10)
$index = 'failover-it-' + [guid]::NewGuid().ToString('N')
$attempted = [System.Collections.Generic.List[object]]::new()
$restoreRequired = $false
$metrics = @{ QueryAttempts = 0; QueryErrors = 0; StaleResponses = 0; FailoverSeconds = $null; RecoverySeconds = $null }
$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

function Invoke-Search([string] $Endpoint, [string] $Method, [string] $Path, $Body = $null) {
    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::new($Method),
        "${Endpoint}/${Path}?api-version=2024-07-01")
    $request.Headers.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $token)
    if ($null -ne $Body) {
        $request.Content = [System.Net.Http.StringContent]::new(
            ($Body | ConvertTo-Json -Depth 10 -Compress), [Text.Encoding]::UTF8, 'application/json')
    }
    $response = $null
    try {
        $response = $client.SendAsync($request).GetAwaiter().GetResult()
        $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if (-not $response.IsSuccessStatusCode) {
            throw [System.Net.Http.HttpRequestException]::new("Search HTTP $([int]$response.StatusCode)", $null, $response.StatusCode)
        }
        if ($text) { return $text | ConvertFrom-Json }
    } finally {
        if ($response) { $response.Dispose() }
        $request.Dispose()
    }
}

function Write-Sentinel($Region, [int] $Version) {
    $result = Invoke-Search $Region.Endpoint POST "indexes/$index/docs/index" @{
        value = @(@{ '@search.action' = 'mergeOrUpload'; id = 'sentinel'; name = 'failover freshness sentinel'; version = $Version })
    }
    if (@($result.value).Count -ne 1 -or -not $result.value[0].status) {
        throw "Sentinel indexing failed in $($Region.Name)."
    }
}

function Test-Ready([string] $Endpoint, [int] $Version) {
    $metrics.QueryAttempts++
    try {
        $result = Invoke-Search $Endpoint POST "indexes/$index/docs/search" @{ search = '*'; top = 2; count = $true }
        if ($result.'@odata.count' -ne 1 -or @($result.value).Count -ne 1 -or
            $result.value[0].id -cne 'sentinel' -or $result.value[0].version -ne $Version) {
            $metrics.StaleResponses++
            return $false
        }
        return $true
    } catch {
        $metrics.QueryErrors++
        Write-Host "Observed query error at ${Endpoint}: $_"
        return $false
    }
}

function Wait-Ready([object[]] $Endpoints, [int] $Version) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    $successes = 0
    do {
        $ready = $true
        foreach ($endpoint in $Endpoints) {
            if (-not (Test-Ready $endpoint $Version)) { $ready = $false }
        }
        if ($ready) { $successes++ } else { $successes = 0 }
        if ($successes -ge $ConsecutiveSuccesses) { return }
        Start-Sleep -Seconds $PollIntervalSeconds
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw "Readiness timed out after $TimeoutSeconds seconds (expected sentinel version $Version): $($Endpoints -join ', ')"
}

function Restore-Network {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        try {
            $null = Invoke-Arm PATCH $target.Id '2023-11-01' @{ properties = @{ publicNetworkAccess = 'enabled' } }
            $state = Invoke-Arm GET $target.Id '2023-11-01'
            if ($state.properties.publicNetworkAccess -ieq 'enabled') { return }
        } catch { Write-Warning "Restoration attempt failed; retrying: $_" }
        Start-Sleep -Seconds $PollIntervalSeconds
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw "URGENT: could not verify network restoration. Restore publicNetworkAccess=enabled manually on $($target.Id)."
}

function Remove-IsolatedIndex($Region) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        try {
            $null = Invoke-Search $Region.Endpoint DELETE "indexes/$index"
            return
        } catch {
            if ($_.Exception.StatusCode -eq [System.Net.HttpStatusCode]::NotFound) { return }
            if ([DateTimeOffset]::UtcNow -ge $deadline) { throw }
            # Public-access restoration can take time to propagate to the data plane.
            Write-Warning "Retrying isolated index cleanup in $($Region.Name): $_"
        }
        Start-Sleep -Seconds $PollIntervalSeconds
    } while ($true)
}

try {
    # Verify data-plane credentials and existing index queries BEFORE any mutation.
    if ($settings.IndexName -notmatch '^[a-z0-9][a-z0-9-]{0,127}$') { throw 'Config Search.IndexName must identify an existing readable index.' }
    foreach ($endpoint in @($regionMap.Values.Endpoint) + @($gatewayUri.GetLeftPart([UriPartial]::Authority))) {
        $null = Invoke-Search $endpoint POST "indexes/$($settings.IndexName)/docs/search" @{ search = '*'; top = 0; count = $true }
    }
    $initialHealth = Get-BackendHealth
    foreach ($hostName in $backendHosts) {
        $servers = @($initialHealth.backendAddressPools.backendHttpSettingsCollection.servers |
            Where-Object { $_.address -ieq $hostName })
        if ($servers.Count -eq 0 -or @($servers | Where-Object { $_.health -ine 'Healthy' }).Count -gt 0) {
            throw "All configured backends must initially be gateway-Healthy; no mutations performed ($hostName)."
        }
    }
    foreach ($region in $regionMap.Values) {
        $attempted.Add($region)
        $null = Invoke-Search $region.Endpoint PUT "indexes/$index" @{
            name = $index
            fields = @(
                @{ name = 'id'; type = 'Edm.String'; key = $true; filterable = $true },
                @{ name = 'name'; type = 'Edm.String'; searchable = $true },
                @{ name = 'version'; type = 'Edm.Int32'; filterable = $true }
            )
        }
        Write-Sentinel $region 1
    }
    $gatewayEndpoint = $gatewayUri.GetLeftPart([UriPartial]::Authority)
    Wait-Ready (@($regionMap.Values.Endpoint) + @($gatewayEndpoint)) 1
    Write-Host "All regions and gateway are query-ready at version 1; disposable index: $index"
    Write-Warning "Disabling PUBLIC access on $($target.Id). Independent recovery: az search service update --ids '$($target.Id)' --public-network-access enabled"
    $restoreRequired = $true # Set before request: a timed-out request may still have applied.
    $outageStart = $stopwatch.Elapsed.TotalSeconds
    $null = Invoke-Arm PATCH $target.Id '2023-11-01' @{ properties = @{ publicNetworkAccess = 'disabled' } }
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    $isolated = $false
    do {
        $rejected = $false
        $metrics.QueryAttempts++
        try { $null = Invoke-Search $target.Endpoint POST "indexes/$index/docs/search" @{ search = '*'; top = 0 } }
        catch {
            $metrics.QueryErrors++
            # Auth was checked before mutation; require explicit rejection, not an arbitrary timeout.
            $rejected = $_.Exception.StatusCode -eq [System.Net.HttpStatusCode]::Forbidden
        }
        $null = Test-Ready $gatewayEndpoint 1
        $health = Get-BackendHealth
        $targetHealth = @($health.backendAddressPools.backendHttpSettingsCollection.servers |
            Where-Object { $_.address -ieq $names[$TargetRegion] })
        $isolated = $rejected -and $targetHealth.Count -gt 0 -and
            @($targetHealth | Where-Object { $_.health -ine 'Unhealthy' }).Count -eq 0
        if (-not $isolated) { Start-Sleep -Seconds $PollIntervalSeconds }
    } while (-not $isolated -and [DateTimeOffset]::UtcNow -lt $deadline)
    if (-not $isolated) { throw 'Did not observe both target query rejection and gateway Unhealthy status; failover is NOT proven.' }
    $healthy = @($regionMap.Values | Where-Object { $_.Name -ine $TargetRegion })
    foreach ($region in $healthy) { Write-Sentinel $region 2 }
    Wait-Ready (@($healthy.Endpoint) + @($gatewayEndpoint)) 2
    $metrics.FailoverSeconds = [math]::Round($stopwatch.Elapsed.TotalSeconds - $outageStart, 2)
    Write-Host "Failover observed with fresh version 2 in surviving regions and gateway. Transient errors are reported, not hidden."

    $recoveryStart = $stopwatch.Elapsed.TotalSeconds
    Restore-Network
    $restoreRequired = $false
    Wait-Ready @($target.Endpoint) 1
    Write-Host 'Recovered target still has version 1: explicitly replaying missed version 2 (no automatic replication claim).'
    Write-Sentinel $target 2
    Wait-Ready (@($regionMap.Values.Endpoint) + @($gatewayEndpoint)) 2
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    $rejoined = $false
    do {
        $health = Get-BackendHealth
        $targetHealth = @($health.backendAddressPools.backendHttpSettingsCollection.servers |
            Where-Object { $_.address -ieq $names[$TargetRegion] })
        $rejoined = $targetHealth.Count -gt 0 -and
            @($targetHealth | Where-Object { $_.health -ine 'Healthy' }).Count -eq 0
        $null = Test-Ready $gatewayEndpoint 2
        if (-not $rejoined) { Start-Sleep -Seconds $PollIntervalSeconds }
    } while (-not $rejoined -and [DateTimeOffset]::UtcNow -lt $deadline)
    if (-not $rejoined) { throw 'Direct queries recovered, but the target did not rejoin the gateway as Healthy.' }
    Wait-Ready (@($regionMap.Values.Endpoint) + @($gatewayEndpoint)) 2
    $metrics.RecoverySeconds = [math]::Round($stopwatch.Elapsed.TotalSeconds - $recoveryStart, 2)
} finally {
    $cleanupErrors = [System.Collections.Generic.List[string]]::new()
    try {
        if ($restoreRequired) {
            try { Restore-Network } catch { $cleanupErrors.Add("$_") }
        }
        foreach ($region in $attempted) {
            try { Remove-IsolatedIndex $region }
            catch {
                $cleanupErrors.Add("Index cleanup failed in $($region.Name) for ${index}: $_")
            }
        }
    } finally {
        $client.Dispose()
        Write-Host ($metrics | ConvertTo-Json)
        Write-Host 'These are sampled observations, not a zero-error availability guarantee.'
    }
    if ($cleanupErrors.Count -gt 0) { throw ($cleanupErrors -join "`n") }
}
