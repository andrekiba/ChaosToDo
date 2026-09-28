using Azure.Provisioning.AppService;
using Azure.Provisioning.Sql;

var builder = DistributedApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Azure SQL Database
// Business Critical + zone redundant so the "Compute Zone Down + SQL DB
// Failover" Chaos Studio scenario has a same-region HA replica to fail over
// to without needing a separate geo-replication failover group.
// ---------------------------------------------------------------------------
var sql = builder.AddAzureSqlServer("sql")
    .RunAsContainer(); // local dev only: runs a SQL Server container instead of provisioning Azure SQL.

var sqlDb = sql.AddDatabase("database");

sql.ConfigureInfrastructure(infra =>
{
    var database = infra.GetProvisionableResources().OfType<SqlDatabase>().Single();
    database.Sku = new SqlSku
    {
        Name = "BC_Gen5",
        Tier = "BusinessCritical",
        Family = "Gen5",
        Capacity = 2,
    };
    database.IsZoneRedundant = true;
});

// ---------------------------------------------------------------------------
// Azure Managed Redis — target of the "Cache Stampede" Chaos Studio scenario.
// ---------------------------------------------------------------------------
var cache = builder.AddAzureManagedRedis("cache")
    .WithAccessKeyAuthentication() // simpler connection string for the demo; prefer Entra ID in production.
    .RunAsContainer(); // local dev only: runs a Redis container instead of provisioning Azure Managed Redis.

// ---------------------------------------------------------------------------
// Azure App Service environment — Premium v3, zone redundant, 2 instances so
// the plan actually spans availability zones (Azure requires at least 2
// worker instances for a zone-redundant Premium v3 plan; 2 is the minimum
// and is enough to demo "Compute Zone Down" — one instance survives).
// This is the other target of "Compute Zone Down" and "Cache Stampede".
// ---------------------------------------------------------------------------
var appServiceEnv = builder.AddAzureAppServiceEnvironment("app-service-env")
    .ConfigureInfrastructure(infra =>
    {
        var plan = infra.GetProvisionableResources().OfType<AppServicePlan>().Single();
        plan.IsZoneRedundant = true;
        plan.Sku = new AppServiceSkuDescription
        {
            Name = "P1v3",
            Tier = "PremiumV3",
            Capacity = 2,
        };
    });

builder.AddProject<Projects.ChaosToDo_Api>("api")
    .WithHttpHealthCheck("/health")
    .WithReference(sqlDb)
    .WithReference(cache)
    .WaitFor(sqlDb)
    .WaitFor(cache)
    .PublishAsAzureAppServiceWebsite((_, _) => { });

builder.Build().Run();
