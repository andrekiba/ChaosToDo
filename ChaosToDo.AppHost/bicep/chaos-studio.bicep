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
// an optional Zones filter — so the three demo scenarios are defined here as custom
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

@description('Name of the Azure SQL logical server.')
param sqlServerName string

@description('Name of the Azure SQL database.')
param sqlDatabaseName string

@description('Name of the Azure Managed Redis resource.')
param redisName string

// Built-in role definition IDs recommended by the Microsoft.Chaos action catalog
// (GET /providers/Microsoft.Chaos/locations/{region}/actions) for each Action used below.
var websiteContributorRoleId = 'de139f84-1756-47ae-9be6-808fbbe84772' // Website Contributor
var sqlDbContributorRoleId = '9b7fa17d-e63e-47b0-bb0a-15c516ac86ec' // SQL DB Contributor
var redisContributorRoleId = '3015e5ed-6856-4ab3-b2f0-b8492aa30ca6' // Azure Managed Redis Contributor

resource existingSite 'Microsoft.Web/sites@2026-08-01' existing = {
  name: apiSiteName
}

resource existingSqlDatabase 'Microsoft.Sql/servers/databases@2025-01-01' existing = {
  name: '${sqlServerName}/${sqlDatabaseName}'
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

resource sqlRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(existingSqlDatabase.id, workspace.id, sqlDbContributorRoleId)
  scope: existingSqlDatabase
  properties: {
    principalId: workspace.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', sqlDbContributorRoleId)
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

// Scenario 2: SQL DB Failover.
// Business Critical + zone-redundant, so this fails over to the local HA replica —
// no geo-replication / failover group needed.
resource sqlDbFailover 'Microsoft.Chaos/workspaces/scenarios@2026-08-01-preview' = {
  parent: workspace
  name: 'sql-db-failover'
  properties: {
    description: 'Forces a planned failover of the Business Critical database to its zone-redundant HA replica.'
    parameters: []
    actions: [
      {
        name: 'db-failover'
        actionId: 'urn:csci:microsoft:sql:failover/1.0.0'
        description: 'Fail over to the local HA replica (planned, no data loss).'
        duration: 'PT5M'
        externalResource: {
          resourceId: existingSqlDatabase.id
        }
        parameters: [
          {
            key: 'ForceAllowDataLoss'
            value: 'false'
          }
        ]
      }
    ]
  }
}

// Scenario 3a: Cache Stampede.
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

// Scenario 3b: Cache Stampede with Process Crash.
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

output workspaceId string = workspace.id
output workspaceName string = workspace.name
