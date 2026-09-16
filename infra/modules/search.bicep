@description('Globally-unique name of the Azure AI Search service (lowercase).')
param name string

@description('Region for this search service.')
param location string

@description('SKU for the search service.')
param sku string = 'basic'

@description('Principal that can query documents.')
param queryPrincipalId string

param queryPrincipalType string = 'User'
@description('Principal that can manage indexes and write documents.')
param indexingPrincipalId string
param indexingPrincipalType string = 'User'
param disableLocalAuth bool = true
param replicaCount int = 1
param partitionCount int = 1
param enablePrivateEndpoints bool = false
param logAnalyticsWorkspaceId string = ''

resource search 'Microsoft.Search/searchServices@2023-11-01' = {
  name: name
  location: location
  sku: {
    name: sku
  }
  properties: {
    replicaCount: replicaCount
    partitionCount: partitionCount
    hostingMode: 'default'
    publicNetworkAccess: enablePrivateEndpoints ? 'disabled' : 'enabled'
    disableLocalAuth: disableLocalAuth
    // authOptions must be omitted when API-key authentication is disabled.
    ...(!disableLocalAuth ? { authOptions: { aadOrApiKey: { aadAuthFailureMode: 'http403' } } } : {})
  }
}

// Built-in role definition IDs for Azure AI Search.
var roleIds = {
  // Manage service objects, including index definitions (not document access).
  searchServiceContributor: '7ca78c08-252a-4471-8644-bb5ff32d4ba0'
  // Write documents (data plane).
  searchIndexDataContributor: '8ebe5a00-799e-43f5-93ac-243d3dce84a7'
  // Query documents (data plane).
  searchIndexDataReader: '1407120a-92aa-4202-b7e9-c0e197c71c8f'
}

var assignments = [
  {
    principalId: queryPrincipalId
    principalType: queryPrincipalType
    roleId: roleIds.searchIndexDataReader
  }
  {
    principalId: indexingPrincipalId
    principalType: indexingPrincipalType
    roleId: roleIds.searchIndexDataContributor
  }
  {
    principalId: indexingPrincipalId
    principalType: indexingPrincipalType
    roleId: roleIds.searchServiceContributor
  }
]

resource roleAssignments 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for assignment in assignments: {
    name: guid(search.id, assignment.principalId, assignment.roleId)
    scope: search
    properties: {
      roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', assignment.roleId)
      principalId: assignment.principalId
      principalType: assignment.principalType
    }
  }
]

resource diagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = if (!empty(logAnalyticsWorkspaceId)) {
  name: 'search-diagnostics'
  scope: search
  properties: {
    workspaceId: logAnalyticsWorkspaceId
    logs: [
      {
        categoryGroup: 'allLogs'
        enabled: true
      }
    ]
    metrics: [
      {
        category: 'AllMetrics'
        enabled: true
      }
    ]
  }
}

output name string = search.name
output fqdn string = '${search.name}.search.windows.net'
output id string = search.id
