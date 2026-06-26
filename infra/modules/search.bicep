@description('Globally-unique name of the Azure AI Search service (lowercase).')
param name string

@description('Region for this search service.')
param location string

@description('SKU for the search service.')
param sku string = 'basic'

@description('Principal that receives data-plane + control-plane RBAC on this service.')
param principalId string

@description('Type of the principal receiving RBAC.')
param principalType string = 'User'

resource search 'Microsoft.Search/searchServices@2023-11-01' = {
  name: name
  location: location
  sku: {
    name: sku
  }
  properties: {
    replicaCount: 1
    partitionCount: 1
    hostingMode: 'default'
    publicNetworkAccess: 'enabled'
    // Enable Microsoft Entra (RBAC) auth so one bearer token works across every region.
    authOptions: {
      aadOrApiKey: {
        aadAuthFailureMode: 'http403'
      }
    }
  }
}

// Built-in role definition IDs for Azure AI Search.
var roleIds = {
  // Create/update indexes (control plane).
  searchServiceContributor: '7ca78c08-252a-4471-8644-bb5ff32d4ba0'
  // Write documents (data plane).
  searchIndexDataContributor: '8ebe5a00-799e-43f5-93ac-243d3dce84a7'
  // Query documents (data plane).
  searchIndexDataReader: '1407120a-92aa-4202-b7e9-c0e197c71c8f'
}

resource roleAssignments 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for role in items(roleIds): {
    name: guid(search.id, principalId, role.value)
    scope: search
    properties: {
      roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', role.value)
      principalId: principalId
      principalType: principalType
    }
  }
]

output name string = search.name
output fqdn string = '${search.name}.search.windows.net'
