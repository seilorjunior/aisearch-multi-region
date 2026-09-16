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

param addressPrefix string = '10.40'
param enablePrivateEndpoints bool = false
@description('When nonempty, restrict origin ingress to Azure Front Door and enforce this profile ID in WAF.')
param frontDoorId string = ''
param originHostName string = ''
param logAnalyticsWorkspaceId string = ''

var appGwName = '${namePrefix}-agw'
var vnetName = '${namePrefix}-vnet'
var pipName = '${namePrefix}-pip'
var subnetName = 'appgw-subnet'

resource nsg 'Microsoft.Network/networkSecurityGroups@2023-11-01' = if (!empty(frontDoorId)) {
  name: '${namePrefix}-origin-nsg'
  location: location
  properties: {
    securityRules: [
      {
        name: 'AllowFrontDoorHttps'
        properties: {
          priority: 100
          direction: 'Inbound'
          access: 'Allow'
          protocol: 'Tcp'
          sourceAddressPrefix: 'AzureFrontDoor.Backend'
          sourcePortRange: '*'
          destinationAddressPrefix: '*'
          destinationPortRange: '443'
        }
      }
      {
        name: 'AllowGatewayManager'
        properties: {
          priority: 110
          direction: 'Inbound'
          access: 'Allow'
          protocol: 'Tcp'
          sourceAddressPrefix: 'GatewayManager'
          sourcePortRange: '*'
          destinationAddressPrefix: '*'
          destinationPortRange: '65200-65535'
        }
      }
      {
        name: 'AllowAzureLoadBalancer'
        properties: {
          priority: 120
          direction: 'Inbound'
          access: 'Allow'
          protocol: '*'
          sourceAddressPrefix: 'AzureLoadBalancer'
          sourcePortRange: '*'
          destinationAddressPrefix: '*'
          destinationPortRange: '*'
        }
      }
      {
        name: 'DenyOtherHttps'
        properties: {
          priority: 130
          direction: 'Inbound'
          access: 'Deny'
          protocol: 'Tcp'
          sourceAddressPrefix: '*'
          sourcePortRange: '*'
          destinationAddressPrefix: '*'
          destinationPortRange: '443'
        }
      }
    ]
  }
}

resource originPolicy 'Microsoft.Network/ApplicationGatewayWebApplicationFirewallPolicies@2023-11-01' = if (!empty(frontDoorId)) {
  name: '${namePrefix}-origin-policy'
  location: location
  properties: {
    policySettings: {
      state: 'Enabled'
      mode: 'Prevention'
      requestBodyCheck: false
    }
    customRules: [
      {
        name: 'BlockOtherFrontDoorProfiles'
        priority: 1
        ruleType: 'MatchRule'
        action: 'Block'
        matchConditions: [
          {
            matchVariables: [
              {
                variableName: 'RequestHeaders'
                selector: 'X-Azure-FDID'
              }
            ]
            operator: 'Equal'
            negationConditon: true
            matchValues: [
              frontDoorId
            ]
          }
        ]
      }
      {
        // This policy gates origin access only; do not apply SQL-injection rules to search query syntax.
        name: 'AllowThisFrontDoorProfile'
        priority: 2
        ruleType: 'MatchRule'
        action: 'Allow'
        matchConditions: [
          {
            matchVariables: [
              {
                variableName: 'RequestHeaders'
                selector: 'X-Azure-FDID'
              }
            ]
            operator: 'Equal'
            negationConditon: false
            matchValues: [
              frontDoorId
            ]
          }
        ]
      }
    ]
    managedRules: {
      managedRuleSets: [
        {
          ruleSetType: 'OWASP'
          ruleSetVersion: '3.2'
        }
      ]
    }
  }
}

resource vnet 'Microsoft.Network/virtualNetworks@2023-11-01' = {
  name: vnetName
  location: location
  properties: {
    addressSpace: {
      addressPrefixes: [
        '${addressPrefix}.0.0/16'
      ]
    }
    subnets: concat([
      {
        name: subnetName
        properties: {
          addressPrefix: '${addressPrefix}.0.0/24'
          ...(!empty(frontDoorId) ? { networkSecurityGroup: { id: nsg!.id } } : {})
        }
      }
    ], enablePrivateEndpoints ? [
      {
        name: 'private-endpoints'
        properties: {
          addressPrefix: '${addressPrefix}.1.0/24'
          privateEndpointNetworkPolicies: 'Disabled'
        }
      }
    ] : [])
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
      name: empty(frontDoorId) ? 'Standard_v2' : 'WAF_v2'
      tier: empty(frontDoorId) ? 'Standard_v2' : 'WAF_v2'
    }
    firewallPolicy: !empty(frontDoorId) ? { id: originPolicy!.id } : null
    sslPolicy: {
      policyType: 'Predefined'
      policyName: 'AppGwSslPolicy20220101S'
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
          requireServerNameIndication: !empty(originHostName)
          ...(!empty(originHostName) ? { hostName: originHostName } : {})
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

resource diagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = if (!empty(logAnalyticsWorkspaceId)) {
  name: 'gateway-diagnostics'
  scope: appgw
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

output fqdn string = pip.properties.dnsSettings.fqdn
output name string = appgw.name
output id string = appgw.id
output publicIpAddress string = pip.properties.ipAddress
output vnetId string = vnet.id
output vnetName string = vnet.name
output privateEndpointSubnetId string = '${vnet.id}/subnets/private-endpoints'
