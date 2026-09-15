@description('Prefix for the gateway, VNet and public IP names.')
param namePrefix string

@description('Region for the Application Gateway (regional resource).')
param location string

@description('DNS label for the public IP.')
param dnsLabel string

@description('FQDNs of the Azure AI Search backends, e.g. mysvc.search.windows.net')
param searchFqdns array

@description('Base64-encoded PFX for the HTTPS listener.')
@secure()
param sslCertData string

@description('Password for the PFX.')
@secure()
param sslCertPassword string

@description('Address prefix for the VNet created for the Application Gateway.')
param vnetAddressPrefix string = '10.40.0.0/16'

@description('Address prefix for the Application Gateway subnet inside the VNet.')
param subnetAddressPrefix string = '10.40.0.0/24'

@description('SKU name for the Application Gateway. Use WAF_v2 for internet-facing workloads.')
@allowed([
  'Standard_v2'
  'WAF_v2'
])
param gatewaySkuName string = 'Standard_v2'

var appGwName = '${namePrefix}-agw'
var vnetName = '${namePrefix}-vnet'
var pipName = '${namePrefix}-pip'
var subnetName = 'appgw-subnet'

resource vnet 'Microsoft.Network/virtualNetworks@2023-11-01' = {
  name: vnetName
  location: location
  properties: {
    addressSpace: {
      addressPrefixes: [
        vnetAddressPrefix
      ]
    }
    subnets: [
      {
        name: subnetName
        properties: {
          addressPrefix: subnetAddressPrefix
        }
      }
    ]
  }
}

resource pip 'Microsoft.Network/publicIPAddresses@2023-11-01' = {
  name: pipName
  location: location
  sku: {
    name: 'Standard'
  }
  properties: {
    publicIPAllocationMethod: 'Static'
    dnsSettings: {
      domainNameLabel: dnsLabel
    }
  }
}

resource appgw 'Microsoft.Network/applicationGateways@2023-11-01' = {
  name: appGwName
  location: location
  properties: {
    sku: {
      name: gatewaySkuName
      tier: gatewaySkuName
    }
    autoscaleConfiguration: {
      minCapacity: 1
      maxCapacity: 2
    }
    gatewayIPConfigurations: [
      {
        name: 'appGwIpConfig'
        properties: {
          subnet: {
            id: '${vnet.id}/subnets/${subnetName}'
          }
        }
      }
    ]
    sslCertificates: [
      {
        name: 'appgw-ssl'
        properties: {
          data: sslCertData
          password: sslCertPassword
        }
      }
    ]
    frontendIPConfigurations: [
      {
        name: 'appGwPublicFrontendIp'
        properties: {
          publicIPAddress: {
            id: pip.id
          }
        }
      }
    ]
    frontendPorts: [
      {
        name: 'port_443'
        properties: {
          port: 443
        }
      }
    ]
    backendAddressPools: [
      {
        name: 'searchBackendPool'
        properties: {
          backendAddresses: [for fqdn in searchFqdns: { fqdn: fqdn }]
        }
      }
    ]
    probes: [
      {
        name: 'searchProbe'
        properties: {
          protocol: 'Https'
          path: '/ping'
          interval: 30
          timeout: 20
          unhealthyThreshold: 3
          pickHostNameFromBackendHttpSettings: true
          minServers: 0
          // /ping is the documented AI Search health endpoint (returns 200 unauthenticated).
          // Using it avoids relying on a 403 match and gives a clean liveness signal.
          match: {
            statusCodes: [
              '200'
            ]
          }
        }
      }
    ]
    backendHttpSettingsCollection: [
      {
        name: 'searchHttpSettings'
        properties: {
          port: 443
          protocol: 'Https'
          cookieBasedAffinity: 'Disabled'
          pickHostNameFromBackendAddress: true
          requestTimeout: 30
          probe: {
            id: resourceId('Microsoft.Network/applicationGateways/probes', appGwName, 'searchProbe')
          }
        }
      }
    ]
    httpListeners: [
      {
        name: 'httpsListener'
        properties: {
          frontendIPConfiguration: {
            id: resourceId(
              'Microsoft.Network/applicationGateways/frontendIPConfigurations',
              appGwName,
              'appGwPublicFrontendIp'
            )
          }
          frontendPort: {
            id: resourceId('Microsoft.Network/applicationGateways/frontendPorts', appGwName, 'port_443')
          }
          protocol: 'Https'
          sslCertificate: {
            id: resourceId('Microsoft.Network/applicationGateways/sslCertificates', appGwName, 'appgw-ssl')
          }
          requireServerNameIndication: false
        }
      }
    ]
    requestRoutingRules: [
      {
        name: 'searchRoutingRule'
        properties: {
          ruleType: 'Basic'
          priority: 100
          httpListener: {
            id: resourceId('Microsoft.Network/applicationGateways/httpListeners', appGwName, 'httpsListener')
          }
          backendAddressPool: {
            id: resourceId('Microsoft.Network/applicationGateways/backendAddressPools', appGwName, 'searchBackendPool')
          }
          backendHttpSettings: {
            id: resourceId(
              'Microsoft.Network/applicationGateways/backendHttpSettingsCollection',
              appGwName,
              'searchHttpSettings'
            )
          }
        }
      }
    ]
  }
}

output fqdn string = pip.properties.dnsSettings.fqdn
output name string = appgw.name
