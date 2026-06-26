targetScope = 'resourceGroup'

@description('Object ID (principal) running the sample app. Gets data-plane RBAC on every search service.')
param principalId string

@description('Type of the principal receiving RBAC. Use ServicePrincipal when deploying from a pipeline.')
@allowed([
  'User'
  'ServicePrincipal'
  'Group'
])
param principalType string = 'User'

@description('Regions to deploy an Azure AI Search service into. Each becomes an Application Gateway backend.')
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

@description('Base64-encoded PFX for the Application Gateway HTTPS listener.')
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

module search 'modules/search.bicep' = [
  for (region, i) in searchRegions: {
    name: 'search-${i}-${region}'
    params: {
      name: toLower('${namePrefix}-${region}')
      location: region
      sku: searchSku
      principalId: principalId
      principalType: principalType
    }
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
  }
}

output gatewayFqdn string = gateway.outputs.fqdn
output gatewayUrl string = 'https://${gateway.outputs.fqdn}'
output indexName string = 'products'
output searchEndpoints array = [
  for i in range(0, length(searchRegions)): {
    region: searchRegions[i]
    name: search[i].outputs.name
    endpoint: 'https://${search[i].outputs.fqdn}'
  }
]
