param namePrefix string
param location string
param existingWorkspaceId string = ''

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = if (empty(existingWorkspaceId)) {
  name: '${namePrefix}-logs'
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

output workspaceId string = empty(existingWorkspaceId) ? workspace!.id : existingWorkspaceId
