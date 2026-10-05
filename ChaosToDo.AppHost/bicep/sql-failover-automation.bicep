param location string
param automationAccountName string
param sqlServerName string
param sqlDatabaseName string
param identityId string
param identityPrincipalId string

resource database 'Microsoft.Sql/servers/databases@2023-08-01' existing = {
  name: '${sqlServerName}/${sqlDatabaseName}'
}

resource failoverRole 'Microsoft.Authorization/roleDefinitions@2022-04-01' = {
  name: guid(resourceGroup().id, automationAccountName, 'sql-local-ha-failover')
  properties: {
    roleName: '${automationAccountName}-database-failover'
    description: 'Read and request primary local HA failover on the assigned database only.'
    type: 'CustomRole'
    assignableScopes: [resourceGroup().id]
    permissions: [{
      actions: [
        'Microsoft.Sql/servers/databases/read'
        'Microsoft.Sql/servers/databases/failover/action'
      ]
      notActions: []
      dataActions: []
      notDataActions: []
    }]
  }
}

resource failoverAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(database.id, identityId, failoverRole.id)
  scope: database
  properties: {
    principalId: identityPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: failoverRole.id
  }
}

// SQL's documented Location header is under /Microsoft.Sql/locations, not the DB.
// Only operation-result reads need the wider RG scope; failover never does.
resource pollingRole 'Microsoft.Authorization/roleDefinitions@2022-04-01' = {
  name: guid(resourceGroup().id, automationAccountName, 'sql-operation-polling')
  properties: {
    roleName: '${automationAccountName}-sql-operation-reader'
    description: 'Read regional SQL database operation results to complete ARM LRO polling.'
    type: 'CustomRole'
    assignableScopes: [resourceGroup().id]
    permissions: [{
      actions: ['Microsoft.Sql/servers/databases/operationResults/read']
      notActions: []
      dataActions: []
      notDataActions: []
    }]
  }
}

resource pollingAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(resourceGroup().id, identityId, pollingRole.id)
  properties: {
    principalId: identityPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: pollingRole.id
  }
}

resource automation 'Microsoft.Automation/automationAccounts@2024-10-23' = {
  name: automationAccountName
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identityId}': {}
    }
  }
  properties: {
    sku: { name: 'Basic' }
    disableLocalAuth: true
    encryption: { keySource: 'Microsoft.Automation' }
    publicNetworkAccess: true
  }
}

resource runtime 'Microsoft.Automation/automationAccounts/runtimeEnvironments@2024-10-23' = {
  parent: automation
  name: 'sql-failover-powershell-74'
  location: location
  properties: {
    description: 'PowerShell 7.4; uses only built-in HTTP/JSON and .NET APIs, no Az modules.'
    runtime: {
      language: 'PowerShell'
      version: '7.4'
    }
    defaultPackages: {}
  }
}

resource runbook 'Microsoft.Automation/automationAccounts/runbooks@2024-10-23' = {
  parent: automation
  name: 'sql-local-ha-failover'
  location: location
  properties: {
    description: 'One requested primary local HA failover, locked to ${database.id}. No geo/zone fault.'
    runbookType: 'PowerShell'
    runtimeEnvironment: runtime.name
    draft: {}
    logProgress: false
    logVerbose: false
  }
}

output automationAccountName string = automation.name
output runbookId string = runbook.id
