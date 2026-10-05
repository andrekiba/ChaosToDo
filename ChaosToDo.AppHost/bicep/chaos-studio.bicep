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
// None of the built-in Scenario templates (see az rest call to
// /providers/Microsoft.Chaos/locations/{region}/actions) cover "App Service zone down"
// directly — PaaS App Service has no zone-shutdown action, only restart/killProcess with
// an optional Zones filter — so the compute/cache scenarios are defined here as custom
// Scenarios composing the closest available Actions.

// Aspire's AddBicepTemplate always forwards the deployment's own `location` (the resource
// group's region, e.g. westeurope) to every custom bicep module, so this parameter can't be
// removed — but it is intentionally NOT used for the Workspace below: westeurope doesn't
// support the preview. chaosWorkspaceLocation is hardcoded instead.
@description('Deployment resource group location. Not used for the Workspace itself — see chaosWorkspaceLocation.')
param location string = 'westeurope'

var chaosWorkspaceLocation = 'northeurope'

@description('Name of the App Service site (the "api" project) to target.')
param apiSiteName string

@description('Name of the Azure Managed Redis resource.')
param redisName string

@description('Name of the Azure Automation Account that hosts the SQL local HA failover runbook.')
param automationAccountName string

// Built-in role definition IDs recommended by the Microsoft.Chaos action catalog
// (GET /providers/Microsoft.Chaos/locations/{region}/actions) for each Action used below.
var websiteContributorRoleId = 'de139f84-1756-47ae-9be6-808fbbe84772' // Website Contributor
var redisContributorRoleId = '3015e5ed-6856-4ab3-b2f0-b8492aa30ca6' // Azure Managed Redis Contributor

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

resource siteRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(existingSite.id, workspace.id, websiteContributorRoleId)
  scope: existingSite
  properties: {
    principalId: workspace.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', websiteContributorRoleId)
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
// App Service has no zone-shutdown Action, so this kills the worker process on every
// instance in the target zone (requires the zone-redundant P1v3 plan, >= 2 instances).
resource computeZoneDown 'Microsoft.Chaos/workspaces/scenarios@2026-08-01-preview' = {
  parent: workspace
  name: 'compute-zone-down'
  properties: {
    description: 'Kills the App Service worker process on every instance in a target availability zone.'
    parameters: [
      {
        name: 'zone'
        type: 'string'
        required: true
        description: 'Availability zone to take down (e.g. "1", "2" or "3").'
      }
    ]
    actions: [
      {
        name: 'kill-zone-processes'
        actionId: 'urn:csci:microsoft:appservice:killprocess/1.0.0'
        description: 'Kill the worker process on all instances in the target zone.'
        duration: 'PT2M'
        externalResource: {
          resourceId: existingSite.id
        }
        parameters: [
          {
            key: 'Zones'
            value: '["%%{parameters.zone}%%"]'
          }
        ]
      }
    ]
  }
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
        actionId: 'urn:csci:microsoft:managedredis:flushdatabase/1.0.0'
        description: 'Flush the Redis Enterprise default database.'
        duration: 'PT2M'
        externalResource: {
          resourceId: existingRedis.id
        }
      }
    ]
  }
}

// Scenario 3: Cache Stampede with Process Crash.
// Same as above, plus killing the App Service worker process at the same time,
// combining a cache-layer failure with a compute failure.
resource cacheStampedeWithProcessCrash 'Microsoft.Chaos/workspaces/scenarios@2026-08-01-preview' = {
  parent: workspace
  name: 'cache-stampede-with-process-crash'
  properties: {
    description: 'Flushes the Redis cache and kills the App Service worker process at the same time.'
    parameters: []
    actions: [
      {
        name: 'flush-cache'
        actionId: 'urn:csci:microsoft:managedredis:flushdatabase/1.0.0'
        duration: 'PT2M'
        externalResource: {
          resourceId: existingRedis.id
        }
      }
      {
        name: 'kill-process'
        actionId: 'urn:csci:microsoft:appservice:killprocess/1.0.0'
        duration: 'PT2M'
        externalResource: {
          resourceId: existingSite.id
        }
      }
    ]
  }
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
        actionId: 'urn:csci:microsoft:automation:startrunbook/1.0.0'
        description: 'Start the published SQL local HA failover runbook; no geo or zone fault is performed.'
        duration: 'PT15M'
        externalResource: {
          resourceId: existingAutomation.id
        }
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
      }
    ]
  }
}

output workspaceId string = workspace.id
output workspaceName string = workspace.name
