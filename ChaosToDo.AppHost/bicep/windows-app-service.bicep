@description('Location passed by the Aspire Azure deployment environment.')
param location string

@description('Name of the Windows API website.')
param apiSiteName string

@description('Name of the Windows App Service plan.')
param apiPlanName string

@description('Number of Premium v3 workers. The plan is not zone redundant: it hosts the cache scenarios only.')
@allowed([
  '1'
  '2'
  '3'
])
param workerCount string = '2'

@description('Azure SQL server name.')
param sqlServerName string

@description('Azure SQL database name.')
param sqlDatabaseName string

@description('Name of the existing Key Vault containing the Redis connection string.')
param redisKeyVaultName string

@description('Vault URI from Aspire Key Vault outputs.')
param redisKeyVaultUri string

@description('Resource ID of the shared application user-assigned identity.')
param identityId string

@description('Principal ID of the shared application identity.')
param identityPrincipalId string

@description('Client ID of the shared application identity.')
param identityClientId string

var keyVaultSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'
var workerCapacity = int(workerCount)
var sqlConnectionString = 'Server=tcp:${sqlServerName}${environment().suffixes.sqlServerHostname},1433;Initial Catalog=${sqlDatabaseName};Encrypt=True;TrustServerCertificate=False;Authentication=Active Directory Default;'
var redisConnectionStringReference = '@Microsoft.KeyVault(SecretUri=${redisKeyVaultUri}secrets/connectionstrings--cache)'

resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: apiPlanName
  location: location
  sku: {
    name: 'P1v3'
    tier: 'PremiumV3'
    size: 'P1v3'
    family: 'Pv3'
    capacity: workerCapacity
  }
  properties: {
    reserved: false
    zoneRedundant: false
    perSiteScaling: false
  }
}

resource site 'Microsoft.Web/sites@2024-04-01' = {
  name: apiSiteName
  location: location
  kind: 'app'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identityId}': {}
    }
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    keyVaultReferenceIdentity: identityId
    siteConfig: {
      alwaysOn: true
      use32BitWorkerProcess: false
      netFrameworkVersion: 'v10.0'
      http20Enabled: true
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      scmMinTlsVersion: '1.2'
      healthCheckPath: '/health'
    }
  }
}

resource ftpPublishingCredentials 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2022-03-01' = {
  parent: site
  name: 'ftp'
  properties: {
    allow: false
  }
}

resource scmPublishingCredentials 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2022-03-01' = {
  parent: site
  name: 'scm'
  properties: {
    allow: false
  }
}

resource appSettings 'Microsoft.Web/sites/config@2022-09-01' = {
  parent: site
  name: 'appsettings'
  properties: {
    ASPNETCORE_ENVIRONMENT: 'Production'
    AZURE_CLIENT_ID: identityClientId
    AZURE_TOKEN_CREDENTIALS: 'ManagedIdentityCredential'
    HealthChecks__ExposeEndpoints: 'true'
    SCM_DO_BUILD_DURING_DEPLOYMENT: 'false'
    Demo__ServedByHeader: 'true'
    ConnectionStrings__database: sqlConnectionString
    ConnectionStrings__cache: redisConnectionStringReference
  }
}

resource redisSecretsUserRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(redisKeyVault.id, identityPrincipalId, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId))
  scope: redisKeyVault
  properties: {
    principalId: identityPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
  }
}

resource redisKeyVault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: redisKeyVaultName
}

output siteName string = site.name
output siteId string = site.id
output siteDefaultHostname string = site.properties.defaultHostName
output siteUrl string = 'https://${site.properties.defaultHostName}'
output planId string = plan.id
