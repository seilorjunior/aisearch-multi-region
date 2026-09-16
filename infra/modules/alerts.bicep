param namePrefix string
param gatewayIds array
param frontDoorProfileId string = ''
param actionGroupIds array = []
@description('Failed gateway requests in five minutes, not a percentage.')
param failedRequestThreshold int = 10
@description('Front Door 5xx response percentage in five minutes.')
param frontDoor5xxPercentageThreshold int = 5

var actions = [for id in actionGroupIds: { actionGroupId: id }]

resource backendHealth 'Microsoft.Insights/metricAlerts@2018-03-01' = [
  for (gatewayId, i) in gatewayIds: {
    name: '${namePrefix}-gateway-${i}-unhealthy'
    location: 'global'
    properties: {
      description: 'A Search backend is unhealthy. /ping checks liveness, not index freshness or query correctness.'
      severity: 2
      enabled: true
      scopes: [
        gatewayId
      ]
      evaluationFrequency: 'PT1M'
      windowSize: 'PT5M'
      criteria: {
        'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
        allOf: [
          {
            name: 'unhealthy'
            metricNamespace: 'Microsoft.Network/applicationGateways'
            metricName: 'UnhealthyHostCount'
            operator: 'GreaterThan'
            threshold: 0
            timeAggregation: 'Average'
            criterionType: 'StaticThresholdCriterion'
          }
        ]
      }
      actions: actions
    }
  }
]

resource gatewayErrors 'Microsoft.Insights/metricAlerts@2018-03-01' = [
  for (gatewayId, i) in gatewayIds: {
    name: '${namePrefix}-gateway-${i}-errors'
    location: 'global'
    properties: {
      description: 'Gateway failed-request count exceeds the configured five-minute threshold.'
      severity: 2
      enabled: true
      scopes: [
        gatewayId
      ]
      evaluationFrequency: 'PT1M'
      windowSize: 'PT5M'
      criteria: {
        'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
        allOf: [
          {
            name: 'failedRequests'
            metricNamespace: 'Microsoft.Network/applicationGateways'
            metricName: 'FailedRequests'
            operator: 'GreaterThan'
            threshold: failedRequestThreshold
            timeAggregation: 'Total'
            criterionType: 'StaticThresholdCriterion'
          }
        ]
      }
      actions: actions
    }
  }
]

resource frontDoorErrors 'Microsoft.Insights/metricAlerts@2018-03-01' = if (!empty(frontDoorProfileId)) {
  name: '${namePrefix}-frontdoor-errors'
  location: 'global'
  properties: {
    description: 'Front Door 5xx response percentage exceeds the configured five-minute threshold.'
    severity: 2
    enabled: true
    scopes: [
      frontDoorProfileId
    ]
    evaluationFrequency: 'PT1M'
    windowSize: 'PT5M'
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [
        {
          name: '5xxPercentage'
          metricNamespace: 'Microsoft.Cdn/profiles'
          metricName: 'Percentage5XX'
          operator: 'GreaterThan'
          threshold: frontDoor5xxPercentageThreshold
          timeAggregation: 'Average'
          criterionType: 'StaticThresholdCriterion'
        }
      ]
    }
    actions: actions
  }
}

resource frontDoorHealth 'Microsoft.Insights/metricAlerts@2018-03-01' = if (!empty(frontDoorProfileId)) {
  name: '${namePrefix}-frontdoor-origins'
  location: 'global'
  properties: {
    description: 'At least one regional gateway origin failed Front Door HTTPS /ping probes.'
    severity: 2
    enabled: true
    scopes: [
      frontDoorProfileId
    ]
    evaluationFrequency: 'PT1M'
    windowSize: 'PT5M'
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [
        {
          name: 'originHealth'
          metricNamespace: 'Microsoft.Cdn/profiles'
          metricName: 'OriginHealthPercentage'
          operator: 'LessThan'
          threshold: 100
          timeAggregation: 'Average'
          criterionType: 'StaticThresholdCriterion'
        }
      ]
    }
    actions: actions
  }
}
