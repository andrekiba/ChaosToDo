using Azure.Provisioning.AppService;
using Azure.Provisioning.Expressions;
using Azure.Provisioning.RedisEnterprise;
using Azure.Provisioning.Sql;

var builder = DistributedApplication.CreateBuilder(args);

var env = builder.Environment.EnvironmentName;
const string projectName = "chaos-todo";

// ---------------------------------------------------------------------------
// Azure SQL Database
// Business Critical + zone redundant so the "Compute Zone Down + SQL DB
// Failover" Chaos Studio scenario has a same-region HA replica to fail over
// to without needing a separate geo-replication failover group.
// ---------------------------------------------------------------------------
var sql = builder.AddAzureSqlServer("sql")
    .RunAsContainer(container => container
        // Data volume + persistent lifetime: the container (and its data) survives across
        // `aspire run` sessions, so migrations/seed data only need to happen once and
        // previously-saved todos are still there on the next local run.
        .WithDataVolume()
        .WithLifetime(ContainerLifetime.Persistent)); // local dev only: runs a SQL Server container instead of provisioning Azure SQL.

var sqlDb = sql.AddDatabase("database");

sql.ConfigureInfrastructure(infra =>
{
    var server = infra.GetProvisionableResources().OfType<SqlServer>().Single();
    server.Name = BicepFunction.Interpolate($"{projectName}-{env}-sql").Compile();
    var database = infra.GetProvisionableResources().OfType<SqlDatabase>().Single();
    database.Name = BicepFunction.Interpolate($"{projectName}-{env}-sqldb").Compile();
    database.Sku = new SqlSku
    {
        Name = "BC_Gen5",
        Tier = "BusinessCritical",
        Family = "Gen5",
        Capacity = 2
    };
    database.IsZoneRedundant = true;
});

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
        var plan = infra.GetProvisionableResources().OfType<AppServicePlan>().Single();
        plan.Name = BicepFunction.Interpolate($"{projectName}-{env}-plan").Compile();
        plan.IsZoneRedundant = true;
        plan.Sku = new AppServiceSkuDescription
        {
            Name = "P1v3",
            Tier = "PremiumV3",
            Capacity = 2
        };
    });

var apiSiteName = $"{projectName}-{env}-api";
var sqlServerName = $"{projectName}-{env}-sql";
var sqlDatabaseName = $"{projectName}-{env}-sqldb";
var redisName = $"{projectName}-{env}-redis";

builder.AddProject<Projects.ChaosToDo_Api>("api")
    .WithHttpHealthCheck("/health")
    .WithExternalHttpEndpoints()
    .WithReference(sqlDb)
    .WithReference(cache)
    .WaitFor(sqlDb)
    .WaitFor(cache)
    .PublishAsAzureAppServiceWebsite( configure:(infra, site) =>
    {
        site.Name = BicepFunction.Interpolate($"{apiSiteName}").Compile();
    });

// ---------------------------------------------------------------------------
// Azure Chaos Studio Workspace + custom Scenarios for the three demo faults.
// Names are resolved deterministically above (rather than through Bicep
// outputs) since every resource name here is already a fixed, compile-time
// known string built from projectName/env.
//
// Publish-only: a plain AddBicepTemplate resource has no RunAsContainer/
// RunAsExisting equivalent, so without this guard `aspire run` would try to
// provision it against real Azure too (like any Azure resource without a
// local substitute) — and it would fail, since the "existing" App Service
// site / SQL database / Redis cache it references don't exist for real in
// run mode (they're local containers / a local process there).
// ---------------------------------------------------------------------------
if (builder.ExecutionContext.IsPublishMode)
{
    builder.AddBicepTemplate("chaos-studio", "bicep/chaos-studio.bicep")
        .WithParameter("apiSiteName", apiSiteName)
        .WithParameter("sqlServerName", sqlServerName)
        .WithParameter("sqlDatabaseName", sqlDatabaseName)
        .WithParameter("redisName", redisName);
}

builder.Build().Run();
