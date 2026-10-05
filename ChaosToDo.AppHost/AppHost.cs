using Aspire.Hosting.Azure;
using Aspire.Hosting.Pipelines;
using Azure.Provisioning;
using Azure.Provisioning.AppService;
using Azure.Provisioning.Expressions;
using Azure.Provisioning.KeyVault;
using Azure.Provisioning.RedisEnterprise;
using Azure.Provisioning.ContainerRegistry;
using Azure.Provisioning.Resources;
using Azure.Provisioning.Sql;
using ChaosToDo.AppHost;

var builder = DistributedApplication.CreateBuilder(args);

var env = builder.Environment.EnvironmentName;
const string projectName = "chaos-todo";
var sqlDatabaseName = $"{projectName}-{env}-sqldb";
var keyVaultName = $"{projectName}-{env}-kv";
var registryName = $"chaostodo{env.ToLowerInvariant()}acr";
var dashboardName = $"{projectName}-{env}-dashboard";

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
    builder.CreateResourceBuilder(keyVault).ConfigureInfrastructure(infra =>
    {
        var vault = infra.GetProvisionableResources().OfType<KeyVaultService>().Single();
        vault.Name = BicepFunction.Interpolate($"{keyVaultName}").Compile();
    });
}

// ---------------------------------------------------------------------------
// Azure App Service environment — Premium v3, zone redundant, 2 instances so
// the plan actually spans availability zones (Azure requires at least 2
// worker instances for a zone-redundant Premium v3 plan; 2 is the minimum
// and is enough to demo "Compute Zone Down" — one instance survives).
// This is the other target of "Compute Zone Down" and "Cache Stampede".
// ---------------------------------------------------------------------------
var appService = builder.AddAzureAppServiceEnvironment("app-service-env")
    .ConfigureInfrastructure(infra =>
    {
        var resources = infra.GetProvisionableResources().ToList();
        var plan = resources.OfType<AppServicePlan>().Single();
        plan.Name = BicepFunction.Interpolate($"{projectName}-{env}-plan").Compile();
        plan.IsZoneRedundant = true;
        plan.Sku = new AppServiceSkuDescription
        {
            Name = "P1v3",
            Tier = "PremiumV3",
            Capacity = 2
        };

        var dashboard = resources.OfType<WebSite>().Single(resource => resource.BicepIdentifier == "dashboard");
        dashboard.Name = BicepFunction.Interpolate($"{dashboardName}").Compile();
        var dashboardIdentity = resources.OfType<Azure.Provisioning.Roles.UserAssignedIdentity>()
            .Single(resource => resource.BicepIdentifier == "app_service_env_contributor_mi");
        dashboardIdentity.Name = BicepFunction.Interpolate($"{dashboardName}-mi").Compile();

        // Aspire's default URI expression is independent of the dashboard's customized site name.
        var dashboardUri = resources.OfType<ProvisioningOutput>()
            .Single(output => output.BicepIdentifier == "AZURE_APP_SERVICE_DASHBOARD_URI");
        dashboardUri.Value = BicepFunction.Interpolate($"https://{dashboard.Name}.azurewebsites.net");
    });

if (builder.ExecutionContext.IsPublishMode)
{
    appService.WithAcrPullIdentity(sharedIdentity);
    var registry = appService.Resource.ContainerRegistry
        ?? throw new InvalidOperationException("The App Service environment must have a container registry.");
    builder.CreateResourceBuilder(registry).ConfigureInfrastructure(infra =>
    {
        var acr = infra.GetProvisionableResources().OfType<ContainerRegistryService>().Single();
        acr.Name = BicepFunction.Interpolate($"{registryName}").Compile();
    });
    sharedIdentity.WithRoleAssignments(builder.CreateResourceBuilder(registry),
        ContainerRegistryBuiltInRole.AcrPull);
}

var apiSiteName = $"{projectName}-{env}-api";
var redisName = $"{projectName}-{env}-redis";

var api = builder.AddProject<Projects.ChaosToDo_Api>("api")
    .WithHttpHealthCheck("/health")
    .WithExternalHttpEndpoints()
    .WithReference(sqlDb)
    .WithReference(cache)
    .WaitFor(sqlDb)
    .WaitFor(cache)
    .PublishAsAzureAppServiceWebsite( configure:(infra, site) =>
    {
        site.Name = BicepFunction.Interpolate($"{apiSiteName}").Compile();
        var identityId = sharedIdentity.Resource.Id.AsProvisioningParameter(infra);
        // Aspire adds separate app and ACR identity references even when both resolve to the same identity.
        site.Identity.UserAssignedIdentities.Clear();
        site.Identity.UserAssignedIdentities[BicepFunction.Interpolate($"{identityId}").Compile().ToString()] =
            new UserAssignedIdentityDetails();
    });

if (builder.ExecutionContext.IsPublishMode)
{
    api.WithAzureUserAssignedIdentity(sharedIdentity)
        .WithEnvironment(context =>
        {
            // The API only uses ConnectionStrings__cache; composing CACHE_URI would read the secret at deploy time.
            context.EnvironmentVariables.Remove("CACHE_HOST");
            context.EnvironmentVariables.Remove("CACHE_PORT");
            context.EnvironmentVariables.Remove("CACHE_PASSWORD");
            context.EnvironmentVariables.Remove("CACHE_URI");
        });
}

// ---------------------------------------------------------------------------
// Azure Chaos Studio Workspace + custom compute/cache Scenarios.
// Names are resolved deterministically above (rather than through Bicep
// outputs) since every resource name here is already a fixed, compile-time
// known string built from projectName/env.
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

    var chaosStudio = builder.AddBicepTemplate("chaos-studio", "bicep/chaos-studio.bicep")
        .WithParameter("apiSiteName", apiSiteName)
        .WithParameter("redisName", redisName)
        .WithParameter("automationAccountName", automation.GetOutput("automationAccountName"));

#pragma warning disable ASPIREPIPELINES001
    const string runbookReadyStep = "publish-sql-local-ha-runbook";
    var subscriptionId = builder.Configuration["Azure:SubscriptionId"]
        ?? throw new InvalidOperationException("Azure:SubscriptionId is required to publish the SQL failover runbook.");
    var resourceGroupName = builder.Configuration["Azure:ResourceGroup"]
        ?? throw new InvalidOperationException("Azure:ResourceGroup is required to publish the SQL failover runbook.");
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
        chaosSteps.DependsOn(readyStep);
        chaosSteps.DependsOn(context.GetSteps(sql.Resource, WellKnownPipelineTags.ProvisionInfrastructure));
        chaosSteps.DependsOn(context.GetSteps(cache.Resource, WellKnownPipelineTags.ProvisionInfrastructure));

        // App Service creates the website deployment target during BeforeStart, not during --list-steps.
        var website = api.Resource.GetDeploymentTargetAnnotation(appService.Resource)?.DeploymentTarget;
        if (website is not null)
        {
            var websiteSteps = context.GetSteps(website, WellKnownPipelineTags.ProvisionInfrastructure).ToArray();
            if (websiteSteps.Length == 0)
            {
                throw new InvalidOperationException("The API website must have a provisioning step before Chaos Studio.");
            }
            chaosSteps.DependsOn(websiteSteps);
        }

        return Task.CompletedTask;
    });
#pragma warning restore ASPIREPIPELINES001
}

builder.Build().Run();