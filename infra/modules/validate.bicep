@description('At least one query and indexing identity must be supplied (or use principalId for the demo).')
@allowed([true])
param identitiesConfigured bool

@description('Front Door requires distinct regional gateways and distinct public DNS origin hostnames, two certificates and explicit confirmation of trusted origin TLS.')
@allowed([true])
param frontDoorConfigured bool

@description('Basic supports at most three replicas and three partitions; Standard supports at most twelve of each and 36 total search units.')
@allowed([true])
param capacitySupported bool

@description('Search regions must be unique, nonempty Azure location names.')
@allowed([true])
param searchRegionsValid bool

@description('Dual-gateway private mode requires Search services in both gateway regions, so each gateway has a local backend and private endpoint.')
@allowed([true])
param privateRegionsConfigured bool

@description('An optional custom gateway hostname must be a DNS name without a scheme, port or path.')
@allowed([true])
param gatewayHostNameValid bool

output valid bool = identitiesConfigured && frontDoorConfigured && capacitySupported && searchRegionsValid && privateRegionsConfigured && gatewayHostNameValid
