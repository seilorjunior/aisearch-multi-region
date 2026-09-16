param primaryVnetName string
param secondaryVnetName string

resource primary 'Microsoft.Network/virtualNetworks@2023-11-01' existing = {
  name: primaryVnetName
}
resource secondary 'Microsoft.Network/virtualNetworks@2023-11-01' existing = {
  name: secondaryVnetName
}
resource forward 'Microsoft.Network/virtualNetworks/virtualNetworkPeerings@2023-11-01' = {
  parent: primary
  name: 'secondary-gateway'
  properties: {
    remoteVirtualNetwork: {
      id: secondary.id
    }
    allowVirtualNetworkAccess: true
    allowForwardedTraffic: false
    allowGatewayTransit: false
    useRemoteGateways: false
  }
}
resource reverse 'Microsoft.Network/virtualNetworks/virtualNetworkPeerings@2023-11-01' = {
  parent: secondary
  name: 'primary-gateway'
  properties: {
    remoteVirtualNetwork: {
      id: primary.id
    }
    allowVirtualNetworkAccess: true
    allowForwardedTraffic: false
    allowGatewayTransit: false
    useRemoteGateways: false
  }
}
