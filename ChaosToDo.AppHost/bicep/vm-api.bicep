// Zone-redundant Linux VM scale set hosting the ChaosToDo API for the Compute Zone Down scenario.
// One Uniform VMSS instance per availability zone (zoneBalance) behind a zone-redundant Standard
// Load Balancer. Chaos Studio's VMSS shutdown action keeps the instances of the target zone
// powered off for the whole action duration, while the load balancer probe removes them from
// rotation. Application code is delivered by VmApiPublisher (blob package + Run Command).

@description('Location passed by the Aspire Azure deployment environment.')
param location string

@description('Name of the VM scale set.')
param vmssName string

@description('Name of the Standard Load Balancer.')
param loadBalancerName string

@description('Name of the zone-redundant public IP used by the load balancer.')
param publicIpName string

@description('DNS label of the load balancer public IP.')
param dnsLabel string

@description('Name of the virtual network.')
param vnetName string

@description('Name of the API subnet network security group.')
param nsgName string

@description('Name of the storage account that stores the API packages.')
param storageAccountName string

@description('Blob container that stores the API packages.')
param packageContainerName string

@description('VM size; must be available in zones 2 and 3 for this subscription.')
param vmSize string

@description('Number of instances; two gives exactly one instance per zone (2 and 3).')
@minValue(2)
@maxValue(2)
param instanceCount int = 2

@description('Local administrator user name. No inbound SSH is allowed; operations use Run Command.')
param adminUsername string = 'chaosadmin'

@secure()
@description('Generated local administrator password.')
param adminPassword string

@description('Bootstrap script template rendered with deployment values.')
param bootstrapScript string

@description('Azure SQL server name.')
param sqlServerName string

@description('Azure SQL database name.')
param sqlDatabaseName string

@description('Vault URI containing the Redis connection string secret.')
param keyVaultUri string

@description('Resource ID of the shared application user-assigned identity.')
param identityId string

@description('Principal ID of the shared application identity.')
param identityPrincipalId string

@description('Client ID of the shared application identity.')
param identityClientId string

@description('Object ID of the deploying principal (filled by Aspire).')
param userPrincipalId string

var apiPort = 8080
var storageBlobDataReaderRoleId = '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1'
var storageBlobDataContributorRoleId = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
var sqlConnectionString = 'Server=tcp:${sqlServerName}${environment().suffixes.sqlServerHostname},1433;Initial Catalog=${sqlDatabaseName};Encrypt=True;TrustServerCertificate=False;Authentication=Active Directory Default;'
var renderedBootstrap = replace(replace(replace(replace(replace(replace(bootstrapScript,
  '{{STORAGE_ACCOUNT}}', storageAccountName),
  '{{PACKAGE_CONTAINER}}', packageContainerName),
  '{{CLIENT_ID}}', identityClientId),
  '{{SQL_CONNECTION}}', sqlConnectionString),
  '{{KEYVAULT_URI}}', keyVaultUri),
  '\r\n', '\n')

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageAccountName
  location: location
  sku: {
    name: 'Standard_ZRS'
  }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    supportsHttpsTrafficOnly: true
    publicNetworkAccess: 'Enabled'
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource packageContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: packageContainerName
  properties: {
    publicAccess: 'None'
  }
}

resource identityBlobReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, identityPrincipalId, storageBlobDataReaderRoleId)
  scope: storage
  properties: {
    principalId: identityPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataReaderRoleId)
  }
}

resource deployerBlobContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, userPrincipalId, storageBlobDataContributorRoleId)
  scope: storage
  properties: {
    principalId: userPrincipalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataContributorRoleId)
  }
}

resource nsg 'Microsoft.Network/networkSecurityGroups@2024-05-01' = {
  name: nsgName
  location: location
  properties: {
    securityRules: [
      {
        name: 'AllowApiHttpInbound'
        properties: {
          description: 'Client traffic forwarded by the load balancer (source IP is preserved).'
          priority: 100
          direction: 'Inbound'
          access: 'Allow'
          protocol: 'Tcp'
          sourceAddressPrefix: 'Internet'
          sourcePortRange: '*'
          destinationAddressPrefix: '*'
          destinationPortRange: string(apiPort)
        }
      }
      {
        name: 'AllowLoadBalancerProbe'
        properties: {
          priority: 110
          direction: 'Inbound'
          access: 'Allow'
          protocol: 'Tcp'
          sourceAddressPrefix: 'AzureLoadBalancer'
          sourcePortRange: '*'
          destinationAddressPrefix: '*'
          destinationPortRange: string(apiPort)
        }
      }
    ]
  }
}

resource vnet 'Microsoft.Network/virtualNetworks@2024-05-01' = {
  name: vnetName
  location: location
  properties: {
    addressSpace: {
      addressPrefixes: [
        '10.40.0.0/16'
      ]
    }
    subnets: [
      {
        name: 'api'
        properties: {
          addressPrefix: '10.40.1.0/24'
          defaultOutboundAccess: false
          networkSecurityGroup: {
            id: nsg.id
          }
        }
      }
    ]
  }
}

resource publicIp 'Microsoft.Network/publicIPAddresses@2024-05-01' = {
  name: publicIpName
  location: location
  sku: {
    name: 'Standard'
    tier: 'Regional'
  }
  zones: [
    '1'
    '2'
    '3'
  ]
  properties: {
    publicIPAllocationMethod: 'Static'
    publicIPAddressVersion: 'IPv4'
    dnsSettings: {
      domainNameLabel: dnsLabel
    }
  }
}

var frontendId = resourceId('Microsoft.Network/loadBalancers/frontendIPConfigurations', loadBalancerName, 'frontend')
var backendPoolId = resourceId('Microsoft.Network/loadBalancers/backendAddressPools', loadBalancerName, 'api')
var probeId = resourceId('Microsoft.Network/loadBalancers/probes', loadBalancerName, 'alive')

resource loadBalancer 'Microsoft.Network/loadBalancers@2024-05-01' = {
  name: loadBalancerName
  location: location
  sku: {
    name: 'Standard'
    tier: 'Regional'
  }
  properties: {
    frontendIPConfigurations: [
      {
        name: 'frontend'
        properties: {
          publicIPAddress: {
            id: publicIp.id
          }
        }
      }
    ]
    backendAddressPools: [
      {
        name: 'api'
      }
    ]
    // Liveness only: /health also checks SQL and Redis, so a dependency fault would
    // otherwise remove every instance from rotation at once.
    probes: [
      {
        name: 'alive'
        properties: {
          protocol: 'Http'
          port: apiPort
          requestPath: '/alive'
          intervalInSeconds: 5
          probeThreshold: 1
        }
      }
    ]
    loadBalancingRules: [
      {
        name: 'http'
        properties: {
          frontendIPConfiguration: {
            id: frontendId
          }
          backendAddressPool: {
            id: backendPoolId
          }
          probe: {
            id: probeId
          }
          protocol: 'Tcp'
          frontendPort: 80
          backendPort: apiPort
          enableFloatingIP: false
          enableTcpReset: true
          idleTimeoutInMinutes: 4
          loadDistribution: 'Default'
          disableOutboundSnat: true
        }
      }
    ]
    // Explicit SNAT for SQL, Redis, Key Vault, Storage and apt (subnet has no default outbound access).
    outboundRules: [
      {
        name: 'outbound'
        properties: {
          frontendIPConfigurations: [
            {
              id: frontendId
            }
          ]
          backendAddressPool: {
            id: backendPoolId
          }
          protocol: 'All'
          allocatedOutboundPorts: 8000
          enableTcpReset: true
          idleTimeoutInMinutes: 4
        }
      }
    ]
  }
}

resource vmss 'Microsoft.Compute/virtualMachineScaleSets@2024-07-01' = {
  name: vmssName
  location: location
  zones: [
    '2'
    '3'
  ]
  sku: {
    name: vmSize
    tier: 'Standard'
    capacity: instanceCount
  }
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identityId}': {}
    }
  }
  properties: {
    orchestrationMode: 'Uniform'
    zoneBalance: true
    platformFaultDomainCount: 1
    overprovision: false
    upgradePolicy: {
      mode: 'Manual'
    }
    virtualMachineProfile: {
      osProfile: {
        computerNamePrefix: 'chaostodo'
        adminUsername: adminUsername
        adminPassword: adminPassword
        customData: base64(renderedBootstrap)
        linuxConfiguration: {
          disablePasswordAuthentication: false
          provisionVMAgent: true
        }
      }
      storageProfile: {
        imageReference: {
          publisher: 'Canonical'
          offer: 'ubuntu-24_04-lts'
          sku: 'server'
          version: 'latest'
        }
        osDisk: {
          createOption: 'FromImage'
          caching: 'ReadWrite'
          diskSizeGB: 30
          managedDisk: {
            storageAccountType: 'StandardSSD_LRS'
          }
        }
        diskControllerType: 'NVMe'
      }
      securityProfile: {
        securityType: 'TrustedLaunch'
        uefiSettings: {
          secureBootEnabled: true
          vTpmEnabled: true
        }
      }
      diagnosticsProfile: {
        bootDiagnostics: {
          enabled: true
        }
      }
      networkProfile: {
        networkInterfaceConfigurations: [
          {
            name: 'nic'
            properties: {
              primary: true
              enableAcceleratedNetworking: false
              ipConfigurations: [
                {
                  name: 'ipconfig'
                  properties: {
                    primary: true
                    subnet: {
                      id: vnet.properties.subnets[0].id
                    }
                    loadBalancerBackendAddressPools: [
                      {
                        id: loadBalancer.properties.backendAddressPools[0].id
                      }
                    ]
                  }
                }
              ]
            }
          }
        ]
      }
    }
  }
  dependsOn: [
    identityBlobReader
    packageContainer
  ]
}

output vmssName string = vmss.name
output vmssId string = vmss.id
output loadBalancerFqdn string = publicIp.properties.dnsSettings.fqdn
output storageAccountName string = storage.name
output packageContainerName string = packageContainer.name
