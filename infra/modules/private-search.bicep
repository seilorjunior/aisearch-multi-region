param namePrefix string
param endpointNetworks array
param searchServiceIds array
param vnetIds array

resource zone 'Microsoft.Network/privateDnsZones@2020-06-01' = {
  name: 'privatelink.search.windows.net'
  location: 'global'
}

resource links 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = [
  for (vnetId, i) in vnetIds: {
    parent: zone
    name: '${namePrefix}-gateway-${i}'
    location: 'global'
    properties: {
      registrationEnabled: false
      virtualNetwork: {
        id: vnetId
      }
    }
  }
]

// Exactly one endpoint per Search service avoids conflicting automatic DNS zone-group records.
// In dual-gateway mode, each gateway region's Search endpoint is local; VNet peering shares access.
resource endpoints 'Microsoft.Network/privateEndpoints@2023-11-01' = [
  for (searchId, i) in searchServiceIds: {
    name: '${namePrefix}-search-${i}-pe'
    location: endpointNetworks[i].location
    properties: {
      subnet: {
        id: endpointNetworks[i].subnetId
      }
      privateLinkServiceConnections: [
        {
          name: 'search'
          properties: {
            privateLinkServiceId: searchId
            groupIds: [
              'searchService'
            ]
          }
        }
      ]
    }
  }
]

resource zoneGroups 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2023-11-01' = [
  for (_, i) in searchServiceIds: {
    parent: endpoints[i]
    name: 'search'
    properties: {
      privateDnsZoneConfigs: [
        {
          name: 'search'
          properties: {
            privateDnsZoneId: zone.id
          }
        }
      ]
    }
  }
]

output privateDnsZoneId string = zone.id
