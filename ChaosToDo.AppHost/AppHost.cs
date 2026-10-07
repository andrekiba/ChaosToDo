using Aspire.Hosting.Azure;
using Aspire.Hosting.Pipelines;
using Azure.Provisioning;
using Azure.Provisioning.Expressions;
using Azure.Provisioning.KeyVault;
using Azure.Provisioning.RedisEnterprise;
using Azure.Provisioning.Resources;
using Azure.Provisioning.Sql;
using ChaosToDo.AppHost;
using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);

#region Variables

// Resource names are shared by the templates and code publishers.
var env = builder.Environment.EnvironmentName;
const string projectName = "chaos-todo";
const string vmPackageContainerName = "api-packages";
const string runbookReadyStep = "publish-sql-local-ha-runbook";
var identityName = $"{projectName}-{env}-mi";
var sqlServerName = $"{projectName}-{env}-sql";
var sqlDatabaseName = $"{projectName}-{env}-sqldb";
var sqlAdminIdentityName = $"{projectName}-{env}-sql-admin-mi";
var redisName = $"{projectName}-{env}-redis";
var keyVaultName = $"{projectName}-{env}-kv";
var apiSiteName = $"{projectName}-{env}-api-win";
var apiPlanName = $"{projectName}-{env}-plan-win";
var vmssName = $"{projectName}-{env}-api-vmss";
var vmLoadBalancerName = $"{projectName}-{env}-api-lb";
var vmPublicIpName = $"{projectName}-{env}-api-pip";
var vmDnsLabel = $"{projectName}-{env}-api-vm";
var vmVnetName = $"{projectName}-{env}-vnet";
var vmNsgName = $"{projectName}-{env}-api-nsg";
var vmStorageAccountName = $"{projectName.Replace("-", string.Empty)}{env}st";
var vmBootstrapPath = Path.Combine(builder.AppHostDirectory, "vm", "chaostodo-api-bootstrap.sh");
var automationAccountName = $"{projectName}-{env}-automation";
var failoverIdentityName = $"{projectName}-{env}-sql-failover-mi";

#endregion

#region Run mode

// Shared resource definitions also form the base of the publish model.
// Shared API identity: SQL access, Redis secrets and VM package downloads.
var sharedIdentity = builder.AddAzureUserAssignedIdentity("identity")
    .ConfigureInfrastructure(infra =>
    {
        var identity = infra.GetProvisionableResources()
            .OfType<Azure.Provisioning.Roles.UserAssignedIdentity>().Single();
        identity.Name = BicepFunction.Interpolate($"{identityName}").Compile();
    });

// ---------------------------------------------------------------------------
// Azure SQL Database
// Business Critical has local HA replicas without zone redundancy.
// Run mode substitutes a persistent SQL container; publish mode keeps Azure SQL.
// ---------------------------------------------------------------------------
var sql = builder.AddAzureSqlServer("sql")
    .RunAsContainer(container => container
        // Preserve local data across Aspire sessions.
        .WithDataVolume()
        .WithLifetime(ContainerLifetime.Persistent));

var sqlDb = sql.AddDatabase("database",
    databaseName: builder.ExecutionContext.IsPublishMode ? sqlDatabaseName : "database");

sql.ConfigureInfrastructure(infra =>
{
    var resources = infra.GetProvisionableResources().ToList();
    var server = resources.OfType<SqlServer>().Single();
    server.Name = BicepFunction.Interpolate($"{sqlServerName}").Compile();
    var database = resources.OfType<SqlDatabase>().Single();
    database.Sku = new SqlSku
    {
        Name = "BC_Gen5",
        Tier = "BusinessCritical",
        Family = "Gen5",
        Capacity = 2
    };
    database.IsZoneRedundant = false;
    database.UseFreeLimit = false;
    var sqlAdminIdentity = resources.OfType<Azure.Provisioning.Roles.UserAssignedIdentity>().Single();
    sqlAdminIdentity.Name = BicepFunction.Interpolate($"{sqlAdminIdentityName}").Compile();
});

// ---------------------------------------------------------------------------
// Azure Managed Redis — target of the "Cache Stampede" Chaos Studio scenario.
// ---------------------------------------------------------------------------
var cache = builder.AddAzureManagedRedis("cache")
    .WithAccessKeyAuthentication() // simpler connection string for the demo; prefer Entra ID in production.
    .RunAsContainer(container => container
        .WithRedisInsight());

cache.ConfigureInfrastructure(infra =>
{
    var redis = infra.GetProvisionableResources().OfType<RedisEnterpriseCluster>().Single();
    redis.Name = BicepFunction.Interpolate($"{redisName}").Compile();
    redis.Sku = new RedisEnterpriseSku
    {
        Name = RedisEnterpriseSkuName.BalancedB0
    };
});

// References inject local connection strings; WaitFor controls local startup,
// not the deployment pipeline dependencies configured below.
var api = builder.AddProject<Projects.ChaosToDo_Api>("api")
    .WithHttpHealthCheck("/health")
    .WithExternalHttpEndpoints()
    .WithReference(sqlDb)
    .WithReference(cache)
    .WaitFor(sqlDb)
    .WaitFor(cache);

#endregion

#region Publish mode

// Everything below is cloud-only. Custom Bicep resources have no local substitute
// and must not be provisioned during aspire run.
if (builder.ExecutionContext.IsPublishMode)
{
    // Deployment inputs are only required in publish mode.
    var subscriptionId = builder.Configuration["Azure:SubscriptionId"]
        ?? throw new InvalidOperationException("Azure:SubscriptionId is required for Windows App Service deployment.");
    var resourceGroupName = builder.Configuration["Azure:ResourceGroup"]
        ?? throw new InvalidOperationException("Azure:ResourceGroup is required for Windows App Service deployment.");
    var vmSize = builder.Configuration["Demo:VmApiSize"] ?? "Standard_D2als_v7";
    var workerCount = builder.Configuration.GetValue("Demo:WindowsAppServiceWorkerCount", 2);
    var redisKeyVault = builder.Resources.OfType<AzureKeyVaultResource>()
        .Single(resource => resource.Name == "cache-kv");
    var runbookSource = File.ReadAllText(
        Path.Combine(builder.AppHostDirectory, "runbooks/sql-local-ha-failover.ps1"));
    if (workerCount is < 1 or > 3)
    {
        throw new InvalidOperationException("Demo:WindowsAppServiceWorkerCount must be between 1 and 3.");
    }

    // Replace automatic API role assignments with the explicit SQL grant.
    sql.ClearDefaultRoleAssignments()
        .WithManagedIdentityDatabaseAccess(sharedIdentity);

    // Reuse the vault generated by Redis access-key authentication.
    builder.CreateResourceBuilder(redisKeyVault)
        .ClearDefaultRoleAssignments()
        .ConfigureInfrastructure(infra =>
        {
            var vault = infra.GetProvisionableResources().OfType<KeyVaultService>().Single();
            vault.Name = BicepFunction.Interpolate($"{keyVaultName}").Compile();
        });

    // The project runs locally, but cloud code is packaged by the custom publishers.
    api.ExcludeFromManifest();

    // Windows hosting for the cache and SQL demos; the publisher uploads the ZIP
    // and verifies readiness separately from infrastructure provisioning.
    var windowsApi = builder.AddBicepTemplate("windows-app-service", "bicep/windows-app-service.bicep")
        .WithParameter("apiSiteName", apiSiteName)
        .WithParameter("apiPlanName", apiPlanName)
        .WithParameter(
            "workerCount",
            workerCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
        .WithParameter("sqlServerName", sqlServerName)
        .WithParameter("sqlDatabaseName", sqlDatabaseName)
        .WithParameter("redisKeyVaultName", keyVaultName)
        .WithParameter(
            "redisKeyVaultUri",
            ((IAzureKeyVaultResource)redisKeyVault).VaultUriOutputReference)
        .WithParameter("identityId", sharedIdentity.Resource.Id)
        .WithParameter("identityPrincipalId", sharedIdentity.Resource.PrincipalId)
        .WithParameter("identityClientId", sharedIdentity.Resource.ClientId);

    WindowsApiPublisher.Register(
        api,
        apiSiteName,
        apiPlanName,
        identityName,
        keyVaultName,
        workerCount,
        subscriptionId,
        resourceGroupName);

    // -----------------------------------------------------------------------
    // Linux VM scale set (one instance per zone) behind a Standard Load Balancer:
    // the target of the Compute Zone Down scenario (VMSS shutdown keeps a zone
    // powered off for the whole action duration).
    // -----------------------------------------------------------------------
    var vmAdminPassword = builder.AddParameter(
        "vm-admin-password",
        new GenerateParameterDefault { MinLength = 32, Special = false, MinLower = 2, MinUpper = 2, MinNumeric = 2 },
        secret: true,
        persist: true);

    var vmApi = builder.AddBicepTemplate("vm-api", "bicep/vm-api.bicep")
        .WithParameter("vmssName", vmssName)
        .WithParameter("loadBalancerName", vmLoadBalancerName)
        .WithParameter("publicIpName", vmPublicIpName)
        .WithParameter("dnsLabel", vmDnsLabel)
        .WithParameter("vnetName", vmVnetName)
        .WithParameter("nsgName", vmNsgName)
        .WithParameter("storageAccountName", vmStorageAccountName)
        .WithParameter("packageContainerName", vmPackageContainerName)
        .WithParameter("vmSize", vmSize)
        .WithParameter("adminPassword", vmAdminPassword)
        .WithParameter("bootstrapScript", VmApiPublisher.ReadBootstrapTemplate(vmBootstrapPath))
        .WithParameter("sqlServerName", sqlServerName)
        .WithParameter("sqlDatabaseName", sqlDatabaseName)
        .WithParameter(
            "keyVaultUri",
            ((IAzureKeyVaultResource)redisKeyVault).VaultUriOutputReference)
        .WithParameter("identityId", sharedIdentity.Resource.Id)
        .WithParameter("identityPrincipalId", sharedIdentity.Resource.PrincipalId)
        .WithParameter("identityClientId", sharedIdentity.Resource.ClientId)
        .WithParameter(AzureBicepResource.KnownParameters.UserPrincipalId);

    VmApiPublisher.Register(api, new VmApiPublisher.Settings(
        vmssName,
        vmPublicIpName,
        vmStorageAccountName,
        vmPackageContainerName,
        identityName,
        keyVaultName,
        sqlServerName,
        sqlDatabaseName,
        vmBootstrapPath,
        subscriptionId,
        resourceGroupName));

    // Dedicated control-plane identity and Automation runbook for SQL local HA.
    // The application identity never receives permission to request a failover.
    var failoverIdentity = builder.AddAzureUserAssignedIdentity("sql-failover-identity")
        .ConfigureInfrastructure(infra =>
        {
            var identity = infra.GetProvisionableResources()
                .OfType<Azure.Provisioning.Roles.UserAssignedIdentity>().Single();
            identity.Name = BicepFunction.Interpolate($"{failoverIdentityName}").Compile();
        });

    var automation = builder.AddBicepTemplate("sql-failover-automation", "bicep/sql-failover-automation.bicep")
        .WithParameter("automationAccountName", automationAccountName)
        .WithParameter("sqlServerName", sqlServerName)
        .WithParameter("sqlDatabaseName", sqlDatabaseName)
        .WithParameter("identityId", failoverIdentity.Resource.Id)
        .WithParameter("identityPrincipalId", failoverIdentity.Resource.PrincipalId);

    // Link targets to template outputs so scenarios use the provisioned resources.
    var chaosStudio = builder.AddBicepTemplate("chaos-studio", "bicep/chaos-studio.bicep")
        .WithParameter("apiSiteName", windowsApi.GetOutput("siteName"))
        .WithParameter("vmssName", vmApi.GetOutput("vmssName"))
        .WithParameter("redisName", redisName)
        .WithParameter("automationAccountName", automation.GetOutput("automationAccountName"));

#pragma warning disable ASPIREPIPELINES001
    // Deployment steps publish runbook content and validate Chaos plans.
    // Neither step starts a job or a fault.
    chaosStudio.WithPipelineStepFactory("refresh-chaos-workspace",
        context => ChaosWorkspacePublisher.RefreshAsync(
            context, subscriptionId, resourceGroupName,
            chaosStudio.GetOutput("workspaceName"), chaosStudio.GetOutput("defaultConfigurations")),
        requiredBy: [WellKnownPipelineSteps.Deploy],
        description: "Refresh discovery, evaluate scenarios, and validate IaC configurations without starting faults.");
    automation.WithPipelineStepFactory(runbookReadyStep,
        context => SqlFailoverRunbookPublisher.PublishAsync(
            context,
            subscriptionId,
            resourceGroupName,
            automationAccountName,
            failoverIdentityName,
            sqlServerName,
            sqlDatabaseName,
            runbookSource),
        requiredBy: [WellKnownPipelineSteps.Deploy],
        description: "Import, publish and verify the SQL local HA runbook (never starts a job).");

    // Explicit deployment edges supplement dependencies inferred from Bicep outputs.
    builder.Pipeline.AddPipelineConfiguration(context =>
    {
        // Automation needs SQL provisioned; Chaos needs the published runbook.
        var automationSteps = context.GetSteps(automation.Resource, WellKnownPipelineTags.ProvisionInfrastructure).ToArray();
        automationSteps.DependsOn(context.GetSteps(sql.Resource, WellKnownPipelineTags.ProvisionInfrastructure));
        var readyStep = context.GetSteps(automation.Resource).Single(step => step.Name == runbookReadyStep);
        foreach (var automationStep in automationSteps)
        {
            readyStep.DependsOn(automationStep.Name);
        }
        var chaosSteps = context.GetSteps(chaosStudio.Resource, WellKnownPipelineTags.ProvisionInfrastructure).ToArray();
        new[] { context.GetSteps(chaosStudio.Resource).Single(step => step.Name == "refresh-chaos-workspace") }
            .DependsOn(chaosSteps);
        chaosSteps.DependsOn(readyStep);
        chaosSteps.DependsOn(context.GetSteps(sql.Resource, WellKnownPipelineTags.ProvisionInfrastructure));
        chaosSteps.DependsOn(context.GetSteps(cache.Resource, WellKnownPipelineTags.ProvisionInfrastructure));

        var windowsApiProvisioningSteps = context.GetSteps(
            windowsApi.Resource,
            WellKnownPipelineTags.ProvisionInfrastructure).ToArray();
        if (windowsApiProvisioningSteps.Length == 0)
        {
            throw new InvalidOperationException("The Windows API Bicep template must have a provisioning step before Chaos Studio.");
        }
        // Windows code deployment waits for infrastructure, data access and secrets.
        var windowsApiDeployStep = context.GetSteps(api.Resource)
            .Single(step => step.Name == "deploy-windows-api");
        new[] { windowsApiDeployStep }.DependsOn(windowsApiProvisioningSteps);
        new[] { windowsApiDeployStep }.DependsOn(
            context.GetSteps(sql.Resource, WellKnownPipelineTags.ProvisionInfrastructure));
        new[] { windowsApiDeployStep }.DependsOn(
            context.GetSteps(cache.Resource, WellKnownPipelineTags.ProvisionInfrastructure));
        new[] { windowsApiDeployStep }.DependsOn(
            context.GetSteps(sharedIdentity.Resource, WellKnownPipelineTags.ProvisionInfrastructure));
        new[] { windowsApiDeployStep }.DependsOn(context.GetSteps(
            redisKeyVault,
            WellKnownPipelineTags.ProvisionInfrastructure));

        chaosSteps.DependsOn(windowsApiProvisioningSteps);
        chaosSteps.DependsOn(context.GetSteps(api.Resource).Single(step => step.Name == "verify-windows-api"));

        // VM bootstrap needs identity, SQL and vault; code deployment also needs Redis.
        var vmApiProvisioningSteps = context.GetSteps(
            vmApi.Resource,
            WellKnownPipelineTags.ProvisionInfrastructure).ToArray();
        if (vmApiProvisioningSteps.Length == 0)
        {
            throw new InvalidOperationException("The VM API Bicep template must have a provisioning step before Chaos Studio.");
        }
        vmApiProvisioningSteps.DependsOn(
            context.GetSteps(sharedIdentity.Resource, WellKnownPipelineTags.ProvisionInfrastructure));
        vmApiProvisioningSteps.DependsOn(
            context.GetSteps(sql.Resource, WellKnownPipelineTags.ProvisionInfrastructure));
        vmApiProvisioningSteps.DependsOn(context.GetSteps(
            redisKeyVault,
            WellKnownPipelineTags.ProvisionInfrastructure));
        var vmApiDeployStep = context.GetSteps(api.Resource)
            .Single(step => step.Name == VmApiPublisher.DeployStep);
        new[] { vmApiDeployStep }.DependsOn(vmApiProvisioningSteps);
        new[] { vmApiDeployStep }.DependsOn(
            context.GetSteps(cache.Resource, WellKnownPipelineTags.ProvisionInfrastructure));
        chaosSteps.DependsOn(vmApiProvisioningSteps);
        chaosSteps.DependsOn(context.GetSteps(api.Resource).Single(step => step.Name == VmApiPublisher.VerifyStep));

        return Task.CompletedTask;
    });
#pragma warning restore ASPIREPIPELINES001
}

#endregion

builder.Build().Run();