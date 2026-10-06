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

var env = builder.Environment.EnvironmentName;
const string projectName = "chaos-todo";
var sqlDatabaseName = $"{projectName}-{env}-sqldb";
var keyVaultName = $"{projectName}-{env}-kv";
var apiSiteName = $"{projectName}-{env}-api-win";
var apiPlanName = $"{projectName}-{env}-plan-win";

var sharedIdentity = builder.AddAzureUserAssignedIdentity("identity")
    .ConfigureInfrastructure(infra =>
    {
        var identity = infra.GetProvisionableResources()
            .OfType<Azure.Provisioning.Roles.UserAssignedIdentity>().Single();
        identity.Name = BicepFunction.Interpolate($"{projectName}-{env}-mi").Compile();
    });

// ---------------------------------------------------------------------------
// Azure SQL Database
// Business Critical has local HA replicas without zone redundancy.
// Local HA failover is driven by a dedicated Automation runbook, not the geo-failover Action.
// ---------------------------------------------------------------------------
var sql = builder.AddAzureSqlServer("sql")
    .RunAsContainer(container => container
        // Data volume + persistent lifetime: the container (and its data) survives across
        // `aspire run` sessions, so migrations/seed data only need to happen once and
        // previously-saved todos are still there on the next local run.
        .WithDataVolume()
        .WithLifetime(ContainerLifetime.Persistent)); // local dev only: runs a SQL Server container instead of provisioning Azure SQL.

var sqlDb = sql.AddDatabase("database",
    databaseName: builder.ExecutionContext.IsPublishMode ? sqlDatabaseName : "database");

sql.ConfigureInfrastructure(infra =>
{
    var resources = infra.GetProvisionableResources().ToList();
    var server = resources.OfType<SqlServer>().Single();
    server.Name = BicepFunction.Interpolate($"{projectName}-{env}-sql").Compile();
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
    sqlAdminIdentity.Name = BicepFunction.Interpolate($"{projectName}-{env}-sql-admin-mi").Compile();
});

if (builder.ExecutionContext.IsPublishMode)
{
    sql.ClearDefaultRoleAssignments()
        .WithManagedIdentityDatabaseAccess(sharedIdentity);
}

// ---------------------------------------------------------------------------
// Azure Managed Redis — target of the "Cache Stampede" Chaos Studio scenario.
// ---------------------------------------------------------------------------
var cache = builder.AddAzureManagedRedis("cache")
    .WithAccessKeyAuthentication() // simpler connection string for the demo; prefer Entra ID in production.
    .RunAsContainer(container => container // local dev only: runs a Redis container instead of provisioning Azure Managed Redis.
        .WithRedisInsight());

cache.ConfigureInfrastructure(infra =>
{
    var redis = infra.GetProvisionableResources().OfType<RedisEnterpriseCluster>().Single();
    redis.Name = BicepFunction.Interpolate($"{projectName}-{env}-redis").Compile();
    redis.Sku = new RedisEnterpriseSku
    {
        Name = RedisEnterpriseSkuName.BalancedB0
    };
});

if (builder.ExecutionContext.IsPublishMode)
{
    // Customize the generated vault to retain Aspire's automatic removal in local container mode.
    var keyVault = builder.Resources.OfType<AzureKeyVaultResource>().Single(resource => resource.Name == "cache-kv");
    builder.CreateResourceBuilder(keyVault)
        .ClearDefaultRoleAssignments()
        .ConfigureInfrastructure(infra =>
    {
        var vault = infra.GetProvisionableResources().OfType<KeyVaultService>().Single();
        vault.Name = BicepFunction.Interpolate($"{keyVaultName}").Compile();
    });
}

var redisName = $"{projectName}-{env}-redis";

var api = builder.AddProject<Projects.ChaosToDo_Api>("api")
    .WithHttpHealthCheck("/health")
    .WithExternalHttpEndpoints()
    .WithReference(sqlDb)
    .WithReference(cache)
    .WaitFor(sqlDb)
    .WaitFor(cache);

IResourceBuilder<AzureBicepResource>? windowsApi = null;
IResourceBuilder<AzureBicepResource>? vmApi = null;
if (builder.ExecutionContext.IsPublishMode)
{
    api.ExcludeFromManifest();

    var redisKeyVault = builder.Resources.OfType<AzureKeyVaultResource>()
        .Single(resource => resource.Name == "cache-kv");
    var workerCount = builder.Configuration.GetValue("Azure:WindowsAppServiceWorkerCount", 2);
    if (workerCount is < 1 or > 3)
    {
        throw new InvalidOperationException("Azure:WindowsAppServiceWorkerCount must be between 1 and 3.");
    }

    windowsApi = builder.AddBicepTemplate("windows-app-service", "bicep/windows-app-service.bicep")
        .WithParameter("apiSiteName", apiSiteName)
        .WithParameter("apiPlanName", apiPlanName)
        .WithParameter(
            "workerCount",
            workerCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
        .WithParameter("sqlServerName", $"{projectName}-{env}-sql")
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
        $"{projectName}-{env}-mi",
        keyVaultName,
        workerCount,
        builder.Configuration["Azure:SubscriptionId"]
            ?? throw new InvalidOperationException("Azure:SubscriptionId is required for Windows App Service deployment."),
        builder.Configuration["Azure:ResourceGroup"]
            ?? throw new InvalidOperationException("Azure:ResourceGroup is required for Windows App Service deployment."));

    // -----------------------------------------------------------------------
    // Linux VM scale set (one instance per zone) behind a Standard Load Balancer:
    // the target of the Compute Zone Down scenario (VMSS shutdown keeps a zone
    // powered off for the whole action duration).
    // -----------------------------------------------------------------------
    var vmssName = $"{projectName}-{env}-api-vmss";
    var vmPublicIpName = $"{projectName}-{env}-api-pip";
    var vmStorageAccountName = $"{projectName.Replace("-", string.Empty)}{env}st";
    const string vmPackageContainerName = "api-packages";
    var vmBootstrapPath = Path.Combine(builder.AppHostDirectory, "vm", "chaostodo-api-bootstrap.sh");
    var vmAdminPassword = builder.AddParameter(
        "vm-admin-password",
        new GenerateParameterDefault { MinLength = 32, Special = false, MinLower = 2, MinUpper = 2, MinNumeric = 2 },
        secret: true,
        persist: true);

    vmApi = builder.AddBicepTemplate("vm-api", "bicep/vm-api.bicep")
        .WithParameter("vmssName", vmssName)
        .WithParameter("loadBalancerName", $"{projectName}-{env}-api-lb")
        .WithParameter("publicIpName", vmPublicIpName)
        .WithParameter("dnsLabel", $"{projectName}-{env}-api-vm")
        .WithParameter("vnetName", $"{projectName}-{env}-vnet")
        .WithParameter("nsgName", $"{projectName}-{env}-api-nsg")
        .WithParameter("storageAccountName", vmStorageAccountName)
        .WithParameter("packageContainerName", vmPackageContainerName)
        .WithParameter("vmSize", builder.Configuration["Azure:VmApiSize"] ?? "Standard_D2als_v7")
        .WithParameter("adminPassword", vmAdminPassword)
        .WithParameter("bootstrapScript", VmApiPublisher.ReadBootstrapTemplate(vmBootstrapPath))
        .WithParameter("sqlServerName", $"{projectName}-{env}-sql")
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
        $"{projectName}-{env}-mi",
        keyVaultName,
        $"{projectName}-{env}-sql",
        sqlDatabaseName,
        vmBootstrapPath,
        builder.Configuration["Azure:SubscriptionId"]!,
        builder.Configuration["Azure:ResourceGroup"]!));
}

// ---------------------------------------------------------------------------
// Azure Chaos Studio Workspace + custom compute/cache Scenarios.
// The API name is linked from the Windows website template output so the
// scenarios target the exact site created by the same publish model.
//
// Publish-only: a plain AddBicepTemplate resource has no RunAsContainer/
// RunAsExisting equivalent, so without this guard `aspire run` would try to
// provision it against real Azure too (like any Azure resource without a
// local substitute) — and it would fail, since the "existing" App Service
// site / Redis cache it references don't exist for real in
// run mode (they're local containers / a local process there).
// ---------------------------------------------------------------------------
if (builder.ExecutionContext.IsPublishMode)
{
    var automationAccountName = $"{projectName}-{env}-automation";
    var failoverIdentity = builder.AddAzureUserAssignedIdentity("sql-failover-identity")
        .ConfigureInfrastructure(infra =>
        {
            var identity = infra.GetProvisionableResources()
                .OfType<Azure.Provisioning.Roles.UserAssignedIdentity>().Single();
            identity.Name = BicepFunction.Interpolate($"{projectName}-{env}-sql-failover-mi").Compile();
        });

    var automation = builder.AddBicepTemplate("sql-failover-automation", "bicep/sql-failover-automation.bicep")
        .WithParameter("automationAccountName", automationAccountName)
        .WithParameter("sqlServerName", $"{projectName}-{env}-sql")
        .WithParameter("sqlDatabaseName", sqlDatabaseName)
        .WithParameter("identityId", failoverIdentity.Resource.Id)
        .WithParameter("identityPrincipalId", failoverIdentity.Resource.PrincipalId);

    var windowsApiResource = windowsApi
        ?? throw new InvalidOperationException("The Windows API Bicep template must be registered in publish mode.");
    var vmApiResource = vmApi
        ?? throw new InvalidOperationException("The VM API Bicep template must be registered in publish mode.");
    var chaosStudio = builder.AddBicepTemplate("chaos-studio", "bicep/chaos-studio.bicep")
        .WithParameter("apiSiteName", windowsApiResource.GetOutput("siteName"))
        .WithParameter("vmssName", vmApiResource.GetOutput("vmssName"))
        .WithParameter("redisName", redisName)
        .WithParameter("automationAccountName", automation.GetOutput("automationAccountName"));

#pragma warning disable ASPIREPIPELINES001
    const string runbookReadyStep = "publish-sql-local-ha-runbook";
    var subscriptionId = builder.Configuration["Azure:SubscriptionId"]
        ?? throw new InvalidOperationException("Azure:SubscriptionId is required to publish the SQL failover runbook.");
    var resourceGroupName = builder.Configuration["Azure:ResourceGroup"]
        ?? throw new InvalidOperationException("Azure:ResourceGroup is required to publish the SQL failover runbook.");
    chaosStudio.WithPipelineStepFactory("refresh-chaos-workspace",
        context => ChaosWorkspacePublisher.RefreshAsync(
            context, subscriptionId, resourceGroupName,
            chaosStudio.GetOutput("workspaceName"), chaosStudio.GetOutput("defaultConfigurations")),
        requiredBy: [WellKnownPipelineSteps.Deploy],
        description: "Refresh discovery, evaluate scenarios, and validate IaC configurations without starting faults.");
    var runbookSource = File.ReadAllText(
        Path.Combine(builder.AppHostDirectory, "runbooks/sql-local-ha-failover.ps1"));
    automation.WithPipelineStepFactory(runbookReadyStep,
        context => SqlFailoverRunbookPublisher.PublishAsync(
            context,
            subscriptionId,
            resourceGroupName,
            automationAccountName,
            $"{projectName}-{env}-sql-failover-mi",
            $"{projectName}-{env}-sql",
            sqlDatabaseName,
            runbookSource),
        requiredBy: [WellKnownPipelineSteps.Deploy],
        description: "Import, publish and verify the SQL local HA runbook (never starts a job).");

    builder.Pipeline.AddPipelineConfiguration(context =>
    {
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
            windowsApiResource.Resource,
            WellKnownPipelineTags.ProvisionInfrastructure).ToArray();
        if (windowsApiProvisioningSteps.Length == 0)
        {
            throw new InvalidOperationException("The Windows API Bicep template must have a provisioning step before Chaos Studio.");
        }
        var windowsApiDeployStep = context.GetSteps(api.Resource)
            .Single(step => step.Name == "deploy-windows-api");
        new[] { windowsApiDeployStep }.DependsOn(windowsApiProvisioningSteps);
        new[] { windowsApiDeployStep }.DependsOn(
            context.GetSteps(sql.Resource, WellKnownPipelineTags.ProvisionInfrastructure));
        new[] { windowsApiDeployStep }.DependsOn(
            context.GetSteps(cache.Resource, WellKnownPipelineTags.ProvisionInfrastructure));
        new[] { windowsApiDeployStep }.DependsOn(
            context.GetSteps(sharedIdentity.Resource, WellKnownPipelineTags.ProvisionInfrastructure));
        var redisKeyVaultResource = builder.Resources.OfType<AzureKeyVaultResource>()
            .Single(resource => resource.Name == "cache-kv");
        new[] { windowsApiDeployStep }.DependsOn(context.GetSteps(
            redisKeyVaultResource,
            WellKnownPipelineTags.ProvisionInfrastructure));

        chaosSteps.DependsOn(windowsApiProvisioningSteps);
        chaosSteps.DependsOn(context.GetSteps(api.Resource).Single(step => step.Name == "verify-windows-api"));

        var vmApiProvisioningSteps = context.GetSteps(
            vmApiResource.Resource,
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
            redisKeyVaultResource,
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

builder.Build().Run();