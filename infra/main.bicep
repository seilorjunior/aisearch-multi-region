targetScope = 'resourceGroup'

@description('Demo fallback identity receiving query, document-write and index-management roles. Leave empty when both dedicated identities are supplied.')
param principalId string = ''

@description('Type of the principal receiving RBAC. Use ServicePrincipal when deploying from a pipeline.')
@allowed([
  'User'
  'ServicePrincipal'
  'Group'
])
param principalType string = 'User'

@description('Query-only identity. Defaults to principalId for backwards-compatible demos.')
param queryPrincipalId string = ''
@allowed(['User', 'ServicePrincipal', 'Group'])
param queryPrincipalType string = 'ServicePrincipal'

@description('Identity receiving Search Index Data Contributor and Search Service Contributor. Defaults to principalId.')
param indexingPrincipalId string = ''
@allowed(['User', 'ServicePrincipal', 'Group'])
param indexingPrincipalType string = 'ServicePrincipal'

@description('Regions to deploy an Azure AI Search service into. Each becomes an Application Gateway backend.')
@minLength(1)
param searchRegions array = [
  'eastus2'
  'westus2'
]

@description('Region for the Application Gateway + VNet (regional resource). Defaults to the resource group location.')
param gatewayLocation string = resourceGroup().location

@description('Stable, lowercase prefix for resource names. Must be globally unique-ish for the search services.')
param namePrefix string = 'aismr${uniqueString(resourceGroup().id)}'

@description('DNS label for the gateway public IP -> <dnsLabel>.<gatewayLocation>.cloudapp.azure.com')
param dnsLabel string = namePrefix

@description('Optional single-gateway custom DNS name matching sslCertData. Point its public DNS at gatewayFqdn. Front Door mode uses primaryOriginHostName instead.')
param gatewayHostName string = ''

@description('Base64-encoded PFX for the Application Gateway HTTPS listener.')
@minLength(1)
@secure()
param sslCertData string

@description('Password for the PFX in sslCertData.')
@secure()
param sslCertPassword string

@description('SKU for each Azure AI Search service.')
@allowed([
  'basic'
  'standard'
  'standard2'
])
param searchSku string = 'basic'

@description('Disable API keys and require Microsoft Entra RBAC. Set false only when API-key compatibility is required.')
param disableLocalAuth bool = true

@minValue(1)
@maxValue(12)
param searchReplicaCount int = 1

@allowed([1, 2, 3, 4, 6, 12])
param searchPartitionCount int = 1

@description('Disable public Search access and provision private endpoints/DNS. Direct clients must have VNet routing and DNS access.')
param enablePrivateEndpoints bool = false

@description('Deploy two regional WAF gateways behind Front Door. Requires trusted origin certificates and public DNS for both origins.')
param enableFrontDoor bool = false

param secondaryGatewayLocation string = ''

@description('Public DNS name pointing to the primary gateway IP, matching the publicly trusted sslCertData certificate.')
param primaryOriginHostName string = ''

@description('Distinct public DNS name pointing to the secondary gateway IP, matching secondarySslCertData.')
param secondaryOriginHostName string = ''

@secure()
param secondarySslCertData string = ''
@secure()
param secondarySslCertPassword string = ''

@description('Set true only after arranging valid public DNS and publicly trusted, unexpired certificates with private keys for BOTH origins. Demo self-signed certificates are not supported.')
param trustedOriginCertificatesConfirmed bool = false

@allowed(['Standard_AzureFrontDoor', 'Premium_AzureFrontDoor'])
param frontDoorSku string = 'Standard_AzureFrontDoor'

@description('Enable gateway, Search and Front Door diagnostics and metric alerts. Action groups are required for notifications.')
param enableMonitoring bool = false
@description('Existing Log Analytics workspace resource ID, or empty to create one when monitoring is enabled.')
param logAnalyticsWorkspaceId string = ''
@description('Existing Azure Monitor action group resource IDs. Empty creates alerts without notifications.')
param alertActionGroupIds array = []
@minValue(1)
param failedRequestThreshold int = 10
@minValue(1)
@maxValue(100)
param frontDoor5xxPercentageThreshold int = 5

func isDnsName(host string) bool => length(host) <= 253 && length(split(host, '.')) >= 2 && length(filter(split(host, '.'), label => empty(label) || length(label) > 63 || startsWith(label, '-') || endsWith(label, '-'))) == 0 && length(filter(range(0, length(host)), i => !contains('abcdefghijklmnopqrstuvwxyz0123456789-.', toLower(substring(host, i, 1))))) == 0

var effectiveQueryPrincipalId = empty(queryPrincipalId) ? principalId : queryPrincipalId
var effectiveIndexingPrincipalId = empty(indexingPrincipalId) ? principalId : indexingPrincipalId
var gatewayResourceIds = concat([gateway.outputs.id], enableFrontDoor ? [secondaryGateway!.outputs.id] : [])
var workspaceId = enableMonitoring ? monitoring!.outputs.workspaceId : ''
var effectiveGatewayHostName = enableFrontDoor ? primaryOriginHostName : (empty(gatewayHostName) ? gateway.outputs.fqdn : gatewayHostName)

// any() defers literal-true type checking to ARM's allowedValues validation at deployment time.
// The guard deployment rejects invalid combinations before dependent infrastructure is created.
module validation 'modules/validate.bicep' = {
  name: 'validate-options'
  params: {
    identitiesConfigured: any(!empty(effectiveQueryPrincipalId) && !empty(effectiveIndexingPrincipalId))
    frontDoorConfigured: any(!enableFrontDoor || !contains([
      !empty(secondaryGatewayLocation)
      toLower(secondaryGatewayLocation) != toLower(gatewayLocation)
      isDnsName(primaryOriginHostName)
      isDnsName(secondaryOriginHostName)
      toLower(primaryOriginHostName) != toLower(secondaryOriginHostName)
      !empty(sslCertData)
      !empty(secondarySslCertData)
      trustedOriginCertificatesConfirmed
    ], false))
    capacitySupported: any(searchReplicaCount * searchPartitionCount <= 36 && (searchSku != 'basic' || (searchReplicaCount <= 3 && searchPartitionCount <= 3)))
    searchRegionsValid: any(length(union(searchRegions, searchRegions)) == length(searchRegions) && length(filter(searchRegions, region => empty(region) || region != toLower(region) || contains(region, ' ') || contains(region, '/') || contains(region, '.'))) == 0)
    privateRegionsConfigured: any(!enablePrivateEndpoints || !enableFrontDoor || (contains(searchRegions, gatewayLocation) && contains(searchRegions, secondaryGatewayLocation)))
    gatewayHostNameValid: any(empty(gatewayHostName) || isDnsName(gatewayHostName))
  }
}

module monitoring 'modules/monitoring.bicep' = if (enableMonitoring) {
  name: 'monitoring'
  params: {
    namePrefix: namePrefix
    location: gatewayLocation
    existingWorkspaceId: logAnalyticsWorkspaceId
  }
  dependsOn: [
    validation
  ]
}

module frontDoor 'modules/frontdoor-profile.bicep' = if (enableFrontDoor) {
  name: 'frontdoor-profile'
  params: {
    namePrefix: namePrefix
    sku: frontDoorSku
    logAnalyticsWorkspaceId: workspaceId
  }
  dependsOn: [
    validation
  ]
}

module search 'modules/search.bicep' = [
  for (region, i) in searchRegions: {
    name: 'search-${i}-${region}'
    params: {
      name: toLower('${namePrefix}-${region}')
      location: region
      sku: searchSku
      queryPrincipalId: effectiveQueryPrincipalId
      queryPrincipalType: empty(queryPrincipalId) ? principalType : queryPrincipalType
      indexingPrincipalId: effectiveIndexingPrincipalId
      indexingPrincipalType: empty(indexingPrincipalId) ? principalType : indexingPrincipalType
      disableLocalAuth: disableLocalAuth
      replicaCount: searchReplicaCount
      partitionCount: searchPartitionCount
      enablePrivateEndpoints: enablePrivateEndpoints
      logAnalyticsWorkspaceId: workspaceId
    }
    dependsOn: [
      validation
    ]
  }
]

module gateway 'modules/appgateway.bicep' = {
  name: 'appgw'
  params: {
    namePrefix: namePrefix
    location: gatewayLocation
    dnsLabel: dnsLabel
    sslCertData: sslCertData
    sslCertPassword: sslCertPassword
    searchFqdns: [for i in range(0, length(searchRegions)): search[i].outputs.fqdn]
    enablePrivateEndpoints: enablePrivateEndpoints
    frontDoorId: enableFrontDoor ? frontDoor!.outputs.frontDoorId : ''
    originHostName: enableFrontDoor ? primaryOriginHostName : gatewayHostName
    logAnalyticsWorkspaceId: workspaceId
  }
}

module secondaryGateway 'modules/appgateway.bicep' = if (enableFrontDoor) {
  name: 'appgw-secondary'
  params: {
    namePrefix: '${namePrefix}-secondary'
    location: secondaryGatewayLocation
    dnsLabel: '${dnsLabel}-secondary'
    sslCertData: secondarySslCertData
    sslCertPassword: secondarySslCertPassword
    searchFqdns: [for i in range(0, length(searchRegions)): search[i].outputs.fqdn]
    addressPrefix: '10.41'
    enablePrivateEndpoints: enablePrivateEndpoints
    frontDoorId: frontDoor!.outputs.frontDoorId
    originHostName: secondaryOriginHostName
    logAnalyticsWorkspaceId: workspaceId
  }
}

module peering 'modules/peer-gateways.bicep' = if (enablePrivateEndpoints && enableFrontDoor) {
  name: 'gateway-peering'
  params: {
    primaryVnetName: gateway.outputs.vnetName
    secondaryVnetName: secondaryGateway!.outputs.vnetName
  }
}

module privateSearch 'modules/private-search.bicep' = if (enablePrivateEndpoints) {
  name: 'private-search'
  params: {
    namePrefix: namePrefix
    searchServiceIds: [for i in range(0, length(searchRegions)): search[i].outputs.id]
    endpointNetworks: [
      for region in searchRegions: enableFrontDoor && region == secondaryGatewayLocation ? {
        location: secondaryGatewayLocation
        subnetId: secondaryGateway!.outputs.privateEndpointSubnetId
      } : {
        location: gatewayLocation
        subnetId: gateway.outputs.privateEndpointSubnetId
      }
    ]
    vnetIds: concat([gateway.outputs.vnetId], enableFrontDoor ? [secondaryGateway!.outputs.vnetId] : [])
  }
  dependsOn: [
    peering
  ]
}

module frontDoorRouting 'modules/frontdoor-routing.bicep' = if (enableFrontDoor) {
  name: 'frontdoor-routing'
  params: {
    profileName: frontDoor!.outputs.name
    endpointName: frontDoor!.outputs.endpointName
    originHostNames: [
      primaryOriginHostName
      secondaryOriginHostName
    ]
  }
  dependsOn: [
    gateway
    secondaryGateway
    privateSearch
  ]
}

module alerts 'modules/alerts.bicep' = if (enableMonitoring) {
  name: 'alerts'
  params: {
    namePrefix: namePrefix
    gatewayIds: gatewayResourceIds
    frontDoorProfileId: enableFrontDoor ? frontDoor!.outputs.id : ''
    actionGroupIds: alertActionGroupIds
    failedRequestThreshold: failedRequestThreshold
    frontDoor5xxPercentageThreshold: frontDoor5xxPercentageThreshold
  }
}

output gatewayFqdn string = gateway.outputs.fqdn
output gatewayUrl string = 'https://${effectiveGatewayHostName}'
output queryEndpoint string = enableFrontDoor ? 'https://${frontDoor!.outputs.endpointHostName}' : 'https://${effectiveGatewayHostName}'
output secondaryGatewayFqdn string = enableFrontDoor ? secondaryGateway!.outputs.fqdn : ''
output frontDoorEndpoint string = enableFrontDoor ? 'https://${frontDoor!.outputs.endpointHostName}' : ''
output frontDoorProfileId string = enableFrontDoor ? frontDoor!.outputs.id : ''
output gatewayIds array = gatewayResourceIds
output gatewayVnetIds array = concat([gateway.outputs.vnetId], enableFrontDoor ? [secondaryGateway!.outputs.vnetId] : [])
output originDnsTargets array = enableFrontDoor ? [
  {
    hostName: primaryOriginHostName
    cname: gateway.outputs.fqdn
    ipAddress: gateway.outputs.publicIpAddress
  }
  {
    hostName: secondaryOriginHostName
    cname: secondaryGateway!.outputs.fqdn
    ipAddress: secondaryGateway!.outputs.publicIpAddress
  }
] : []
output privateEndpointsEnabled bool = enablePrivateEndpoints
output privateDnsZoneId string = enablePrivateEndpoints ? privateSearch!.outputs.privateDnsZoneId : ''
output monitoringWorkspaceId string = workspaceId
output indexName string = 'products'
output searchEndpoints array = [
  for i in range(0, length(searchRegions)): {
    region: searchRegions[i]
    name: search[i].outputs.name
    endpoint: 'https://${search[i].outputs.fqdn}'
  }
]
