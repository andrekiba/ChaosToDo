// Custom Azure Chaos Studio Workspace + Scenarios for the ChaosToDo demo.
//
// Chaos Studio Workspaces/Scenarios (the current resource model, replacing the classic
// Experiments/Targets/Capabilities model) is in public preview and, as of writing, only
// buildable in: East US 2, West US 2, West Central US, North Europe, Sweden Central,
// UK South, Japan East — see
// https://learn.microsoft.com/azure/chaos-studio/chaos-studio-region-availability
// (westeurope is NOT in that list). A Workspace is a logical resource that can target
// resources in ANY region, so it does not need to live in the same region as the
// App Service / SQL / Redis it drives — hence North Europe here, close to West Europe.
//
// Targets:
// - Compute Zone Down: the Linux VM scale set (one instance per zone, behind a
//   zone-redundant Standard Load Balancer). The VMSS shutdown action is Cancelable:
//   instances in the selected logical zone stay powered off for the whole duration
//   and are started again when the action ends.
// - Cache scenarios: Azure Managed Redis and the (non zonal) Windows App Service.
//   App Service Kill Process is a discrete action: the process is restarted at once.
//
// Every scenario ships with a `default` configuration (resource targeting and
// parameter values), so the scenarios can be started without portal edits.

// Aspire's AddBicepTemplate always forwards the deployment's own `location` (the resource
// group's region, e.g. westeurope) to every custom bicep module, so this parameter can't be
// removed — but it is intentionally NOT used for the Workspace below: westeurope doesn't
// support the preview. chaosWorkspaceLocation is hardcoded instead.
@description('Deployment resource group location. Not used for the Workspace itself — see chaosWorkspaceLocation.')
param location string = 'westeurope'

var chaosWorkspaceLocation = 'northeurope'

@description('Name output by the Windows API App Service template.')
param apiSiteName string

@description('Name of the Linux API VM scale set targeted by the Compute Zone Down scenario.')
param vmssName string

@description('Logical zone powered off by the default Compute Zone Down configuration.')
@allowed([
  '2'
  '3'
])
param defaultZoneDownZone string = '2'

@description('Name of the Azure Managed Redis resource.')
param redisName string

@description('Name of the Azure Automation Account that hosts the SQL local HA failover runbook.')
param automationAccountName string

// Built-in role definition IDs recommended by the Microsoft.Chaos action catalog
// (GET /providers/Microsoft.Chaos/locations/{region}/actions) for each Action used below.
var websiteContributorRoleId = 'de139f84-1756-47ae-9be6-808fbbe84772' // Website Contributor
var redisContributorRoleId = '3015e5ed-6856-4ab3-b2f0-b8492aa30ca6' // Azure Managed Redis Contributor
var readerRoleId = 'acdd72a7-3385-48ef-bd42-f606fba81ae7' // Reader
var virtualMachineContributorRoleId = '9980e02c-c2be-4d73-94e8-173b1dc7cf3c' // Virtual Machine Contributor

resource existingVmss 'Microsoft.Compute/virtualMachineScaleSets@2024-07-01' existing = {
  name: vmssName
}

resource existingAutomation 'Microsoft.Automation/automationAccounts@2024-10-23' existing = {
  name: automationAccountName
}

resource existingSite 'Microsoft.Web/sites@2026-08-01' existing = {
  name: apiSiteName
}

resource existingRedis 'Microsoft.Cache/redisEnterprise@2026-09-01' existing = {
  name: redisName
}

resource workspace 'Microsoft.Chaos/workspaces@2026-08-01-preview' = {
  name: 'chaostodo-workspace'
  location: chaosWorkspaceLocation
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    scopes: [
      resourceGroup().id
    ]
  }
}

resource workspaceScopeReaderRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(resourceGroup().id, workspace.id, readerRoleId)
  scope: resourceGroup()
  properties: {
    principalId: workspace.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', readerRoleId)
  }
}

resource siteRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(existingSite.id, workspace.id, websiteContributorRoleId)
  scope: existingSite
  properties: {
    principalId: workspace.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', websiteContributorRoleId)
  }
}

resource vmssRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(existingVmss.id, workspace.id, virtualMachineContributorRoleId)
  scope: existingVmss
  properties: {
    principalId: workspace.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', virtualMachineContributorRoleId)
  }
}

resource redisRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(existingRedis.id, workspace.id, redisContributorRoleId)
  scope: existingRedis
  properties: {
    principalId: workspace.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', redisContributorRoleId)
  }
}

resource automationRunbookRole 'Microsoft.Authorization/roleDefinitions@2022-04-01' = {
  name: guid(resourceGroup().id, automationAccountName, 'chaos-studio-start-runbook')
  properties: {
    roleName: '${automationAccountName}-runbook-runner'
    description: 'Allow the Chaos Studio Workspace to inspect and start runbooks in this Automation Account.'
    type: 'CustomRole'
    assignableScopes: [resourceGroup().id]
    permissions: [{
      actions: [
        'Microsoft.Authorization/*/read'
        'Microsoft.Automation/automationAccounts/jobs/read'
        'Microsoft.Automation/automationAccounts/jobs/stop/action'
        'Microsoft.Automation/automationAccounts/jobs/streams/read'
        'Microsoft.Automation/automationAccounts/jobs/suspend/action'
        'Microsoft.Automation/automationAccounts/jobs/write'
        'Microsoft.Automation/automationAccounts/runbooks/read'
      ]
      notActions: []
      dataActions: []
      notDataActions: []
    }]
  }
}

resource automationRunbookRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(existingAutomation.id, workspace.id, automationRunbookRole.id)
  scope: existingAutomation
  properties: {
    principalId: workspace.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: automationRunbookRole.id
  }
}

// Scenario 1: Compute Zone Down.
// Powers off the VM scale set instances in one logical zone for the whole duration
// (Cancelable action), then starts them again. The Load Balancer health probe removes
// the stopped instance, so the API keeps answering from the surviving zones.
resource computeZoneDown 'Microsoft.Chaos/workspaces/scenarios@2026-08-01-preview' = {
  parent: workspace
  name: 'compute-zone-down'
  properties: {
    description: 'Powers off the API VM scale set instances in the selected logical zone for the whole duration.'
    parameters: [
      {
        name: 'duration'
        type: 'string'
        required: false
        default: 'PT2M'
        description: 'How long the zone stays powered off (ISO 8601 duration).'
      }
      {
        name: 'zone'
        type: 'string'
        required: true
        description: 'Logical availability zone of the VM scale set to power off ("2" or "3").'
      }
    ]
    actions: [
      {
        name: 'vmssZoneShutdown'
        actionId: 'microsoft-virtualMachineScaleSet-shutdown/1.0'
        description: 'Power off the VM scale set instances in the selected zone.'
        duration: '%%{parameters.duration}%%'
        parameters: [
          {
            key: 'Zones'
            value: '["%%{parameters.zone}%%"]'
          }
          {
            key: 'GracefulShutdown'
            value: 'false'
          }
        ]
        runAfter: {
          behavior: 'All'
          items: []
        }
      }
    ]
  }
}

var computeZoneDownConfiguration = {
  scenarioId: computeZoneDown.name
  parameters: [
    {
      key: 'duration'
      value: 'PT2M'
    }
    {
      key: 'zone'
      value: defaultZoneDownZone
    }
  ]
  resourceTargeting: {
    include: {
      resources: [
        existingVmss.id
      ]
    }
  }
}

resource computeZoneDownDefault 'Microsoft.Chaos/workspaces/scenarios/configurations@2026-08-01-preview' = {
  parent: computeZoneDown
  name: 'default'
  properties: computeZoneDownConfiguration
}

// Scenario 2: Cache Stampede.
// Flushes Redis so every concurrent reader falls through to the origin at once,
// exercising FusionCache's stampede protection.
resource cacheStampede 'Microsoft.Chaos/workspaces/scenarios@2026-08-01-preview' = {
  parent: workspace
  name: 'cache-stampede'
  properties: {
    description: 'Flushes the Azure Managed Redis cache to trigger a stampede of concurrent origin reads.'
    parameters: []
    actions: [
      {
        name: 'flush-cache'
        actionId: 'microsoft-managedRedis-FlushDatabase/1.0'
        description: 'Flush the Azure Managed Redis default database.'
        duration: 'PT2M'
        parameters: []
        runAfter: {
          behavior: 'All'
          items: []
        }
      }
    ]
  }
}

var cacheStampedeConfiguration = {
  scenarioId: cacheStampede.name
  parameters: []
  resourceTargeting: {
    include: {
      resources: [
        existingRedis.id
      ]
    }
  }
}

resource cacheStampedeDefault 'Microsoft.Chaos/workspaces/scenarios/configurations@2026-08-01-preview' = {
  parent: cacheStampede
  name: 'default'
  properties: cacheStampedeConfiguration
}

// Scenario 3: Cache Stampede with Process Crash.
// Flushes Redis, then kills the Windows App Service API process once the flush has
// completed, so the restarted process starts with an empty L1 and an empty Redis.
// Both actions are discrete. ProcessName is not set: the action's ProcessName filter
// does not match the out-of-process API worker.
resource cacheStampedeWithProcessCrash 'Microsoft.Chaos/workspaces/scenarios@2026-08-01-preview' = {
  parent: workspace
  name: 'cache-stampede-with-process-crash'
  properties: {
    description: 'Flushes Redis, then kills the Windows API worker process.'
    parameters: []
    actions: [
      {
        name: 'flush-cache'
        actionId: 'microsoft-managedRedis-FlushDatabase/1.0'
        description: 'Flush the Azure Managed Redis default database.'
        duration: 'PT2M'
        parameters: []
        runAfter: {
          behavior: 'All'
          items: []
        }
      }
      {
        name: 'killAppServiceProcess'
        actionId: 'microsoft-appService-KillProcess/1.0'
        description: 'Kill the Windows App Service worker process after the flush.'
        duration: 'PT2M'
        parameters: []
        runAfter: {
          behavior: 'All'
          items: [
            {
              type: 'Action'
              name: 'flush-cache'
              onActionLifecycle: 'Success'
            }
          ]
        }
      }
    ]
  }
}

var cacheStampedeWithProcessCrashConfiguration = {
  scenarioId: cacheStampedeWithProcessCrash.name
  parameters: []
  resourceTargeting: {
    include: {
      resources: [
        existingRedis.id
        existingSite.id
      ]
    }
  }
}

resource cacheStampedeWithProcessCrashDefault 'Microsoft.Chaos/workspaces/scenarios/configurations@2026-08-01-preview' = {
  parent: cacheStampedeWithProcessCrash
  name: 'default'
  properties: cacheStampedeWithProcessCrashConfiguration
}

// Cache Stampede with App Service Restart.
// Both discrete actions are eligible to start in parallel; their actual execution
// times need not coincide, so an empty Redis at app startup is not guaranteed.
resource cacheStampedeWithRestart 'Microsoft.Chaos/workspaces/scenarios@2026-08-01-preview' = {
  parent: workspace
  name: 'cache-stampede-with-restart'
  properties: {
    description: 'Flushes Redis and restarts the Windows App Service in parallel.'
    parameters: []
    actions: [
      {
        name: 'flush-cache'
        actionId: 'microsoft-managedRedis-FlushDatabase/1.0'
        description: 'Flush the Azure Managed Redis default database.'
        duration: 'PT2M'
        parameters: []
        runAfter: {
          behavior: 'All'
          items: []
        }
      }
      {
        name: 'restartAppService'
        actionId: 'microsoft-appService-Restart/1.0'
        description: 'Restart the Windows App Service in parallel with the Redis flush.'
        duration: 'PT2M'
        parameters: [
          {
            key: 'SoftRestart'
            value: 'false'
          }
        ]
        runAfter: {
          behavior: 'All'
          items: []
        }
      }
    ]
  }
}

var cacheStampedeWithRestartConfiguration = {
  scenarioId: cacheStampedeWithRestart.name
  parameters: []
  resourceTargeting: {
    include: {
      resources: [
        existingRedis.id
        existingSite.id
      ]
    }
  }
}

resource cacheStampedeWithRestartDefault 'Microsoft.Chaos/workspaces/scenarios/configurations@2026-08-01-preview' = {
  parent: cacheStampedeWithRestart
  name: 'default'
  properties: cacheStampedeWithRestartConfiguration
}

// Scenario 4: SQL local HA failover.
// The StartRunbook Action starts a published Automation runbook. The runbook
// submits one SQL Database primary failover request and waits for its ARM LRO.
resource sqlLocalHaFailover 'Microsoft.Chaos/workspaces/scenarios@2026-08-01-preview' = {
  parent: workspace
  name: 'sql-local-ha-failover'
  properties: {
    description: 'Requests one coordinated primary local HA failover on the target SQL Business Critical database.'
    parameters: []
    actions: [
      {
        name: 'start-sql-local-ha-failover'
        actionId: 'microsoft-Automation-StartRunbook/1.0'
        description: 'Start the published SQL local HA failover runbook; no geo or zone fault is performed.'
        duration: 'PT15M'
        parameters: [
          {
            key: 'RunbookName'
            value: 'sql-local-ha-failover'
          }
          {
            key: 'RunbookParameters'
            value: '{}'
          }
        ]
        runAfter: {
          behavior: 'All'
          items: []
        }
      }
    ]
  }
}

var sqlLocalHaFailoverConfiguration = {
  scenarioId: sqlLocalHaFailover.name
  parameters: []
  resourceTargeting: {
    include: {
      resources: [
        existingAutomation.id
      ]
    }
  }
}

resource sqlLocalHaFailoverDefault 'Microsoft.Chaos/workspaces/scenarios/configurations@2026-08-01-preview' = {
  parent: sqlLocalHaFailover
  name: 'default'
  properties: sqlLocalHaFailoverConfiguration
}

output workspaceId string = workspace.id
output workspaceName string = workspace.name
output defaultConfigurations string = string([
  computeZoneDownConfiguration
  cacheStampedeConfiguration
  cacheStampedeWithProcessCrashConfiguration
  cacheStampedeWithRestartConfiguration
  sqlLocalHaFailoverConfiguration
])
