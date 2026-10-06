using System.Text.Json;
using ChaosToDo.Api;
using ChaosToDo.Api.Data;
using ChaosToDo.Api.Models;
using ChaosToDo.ServiceDefaults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Scalar.AspNetCore;
using ZiggyCreatures.Caching.Fusion;
using ZiggyCreatures.Caching.Fusion.Serialization.SystemTextJson;

var builder = WebApplication.CreateBuilder(args);

// Aspire service defaults: OpenTelemetry, health checks, service discovery, resilience.
builder.AddServiceDefaults();

// VM hosting only: App Service resolves the Redis secret through a Key Vault reference,
// while the VM scale set provides only the vault URI and loads the secret at startup.
if (!string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("keyvault")))
{
    builder.Configuration.AddAzureKeyVaultSecrets("keyvault");
}

// --- EF Core / Azure SQL --------------------------------------------------
// "database" must match the name given to sql.AddDatabase("database") in the AppHost.
builder.AddSqlServerDbContext<TodoDbContext>("database");

// --- Redis / FusionCache ---------------------------------------------------
// "cache" must match the name given to builder.AddAzureManagedRedis("cache") in the
// AppHost. The AppHost configures access-key authentication for that resource
// (see AppHost.cs) so the connection string below is usable directly both by the
// L2 distributed cache and by the FusionCache backplane, without extra Entra ID
// token-credential wiring — a deliberate simplification for a conference demo.
var redisConnectionString = builder.Configuration.GetConnectionString("cache");

builder.Services.AddStackExchangeRedisCache(options =>
{
    options.Configuration = redisConnectionString;
});

// A deliberately slow "downstream" simulation makes factory executions visible.
// A Redis flush alone does not clear FusionCache's in-process L1 or fail-safe entries.
var simulatedDbLatency = builder.Configuration.GetValue("Demo:SimulatedDbLatencyMs", 800);

// Duration: create/update/delete invalidate the list explicitly, so a long duration is safe.
// Eager refresh renews the entry in the background after 90% of its duration, so readers
// never wait for the factory (or the 500 ms soft timeout) when the entry expires.
builder.Services.AddFusionCache()
    .WithDefaultEntryOptions(options => options
        .SetDuration(TimeSpan.FromMinutes(5))
        .SetEagerRefresh(0.9f)
        .SetFailSafe(true, TimeSpan.FromHours(2))
        .SetFactoryTimeouts(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(5)))
    .WithSerializer(new FusionCacheSystemTextJsonSerializer())
    // Reuses the IDistributedCache registered above by AddStackExchangeRedisCache (L2):
    // A Redis flush clears L2; existing L1 entries remain until invalidated or expired.
    .WithRegisteredDistributedCache()
    .WithStackExchangeRedisBackplane(options => options.Configuration = redisConnectionString);

builder.Services.AddOpenApi();

var app = builder.Build();

// Demo diagnostics: which instance/process answered and how many todo-list queries that
// process has run so far. Counters are read when the response starts, so they include
// the query made by the current request.
if (app.Configuration.GetValue<bool>("Demo:ServedByHeader"))
{
    var servedBy = await ServedByIdentity.ResolveAsync();
    var processId = Environment.ProcessId.ToString();
    app.Use((context, next) =>
    {
        context.Response.Headers["X-Served-By"] = servedBy;
        context.Response.Headers["X-Process-Id"] = processId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers["X-Db-Queries"] = DemoQueryCounter.HeaderValue;
            return Task.CompletedTask;
        });
        return next(context);
    });
}

app.MapDefaultEndpoints();

// Mapped unconditionally (not just in Development) so Scalar is also reachable on the
// deployed Azure App Service during the conference demo.
app.MapOpenApi();
app.MapScalarApiReference(); // UI at /scalar/v1

// Create the schema and seed a few rows so the demo has data from the first run.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<TodoDbContext>();
    await db.Database.MigrateAsync();

    if (!await db.Todos.AnyAsync())
    {
        db.Todos.AddRange(
            new TodoItem { Title = "Leggere la documentazione di Azure Chaos Studio" },
            new TodoItem { Title = "Preparare la demo per Azure AI Day Torino" },
            new TodoItem { Title = "Configurare il failover group di Azure SQL" });
        await db.SaveChangesAsync();
    }
}

var todos = app.MapGroup("/api/todos").WithTags("Todos");

// GET /api/todos — goes through FusionCache. On a warm cache this is instant;
// on a cache miss, concurrent callers share a factory execution within each API
// instance. Flushing Redis alone does not guarantee a miss in the in-process L1.
// The factory opens its own DI scope: after the soft timeout the request returns the
// stale value and disposes its scoped DbContext, while the factory keeps running.
todos.MapGet("/", async (IFusionCache cache, IServiceScopeFactory scopes) =>
{
    var items = await cache.GetOrSetAsync("todos:all", async ct =>
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TodoDbContext>();
        await Task.Delay(simulatedDbLatency, ct);
        var result = await db.Todos.AsNoTracking()
            .TagWith("fusion")
            .OrderBy(t => t.CreatedAtUtc)
            .ToListAsync(ct);
        DemoQueryCounter.Fusion();
        return result;
    });

    return Results.Ok(items);
});

// GET /api/todos/nocache — same query, no FusionCache in front. Use this
// endpoint under load *before* introducing FusionCache to show the stampede
// hitting Azure SQL directly, then switch back to "/" to show the fix.
todos.MapGet("/nocache", async (TodoDbContext db, CancellationToken ct) =>
{
    await Task.Delay(simulatedDbLatency, ct);
    var items = await db.Todos.AsNoTracking()
        .TagWith("nocache")
        .OrderBy(t => t.CreatedAtUtc)
        .ToListAsync(ct);
    DemoQueryCounter.NoCache();
    return Results.Ok(items);
});

// GET /api/todos/naive — classic cache-aside on Redis only, deliberately WITHOUT
// stampede protection: no lock, no in-memory L1, no fail-safe. After a Redis flush,
// every concurrent request misses at the same time and runs its own database query.
todos.MapGet("/naive", async (IDistributedCache redis, TodoDbContext db, HttpContext http, CancellationToken ct) =>
{
    var cached = await redis.GetAsync(NaiveCache.TodosKey, ct);
    if (cached is not null)
    {
        http.Response.Headers["X-Cache"] = "HIT";
        return Results.Ok(JsonSerializer.Deserialize<List<TodoItem>>(cached));
    }

    http.Response.Headers["X-Cache"] = "MISS";
    await Task.Delay(simulatedDbLatency, ct);
    var items = await db.Todos.AsNoTracking()
        .TagWith("naive")
        .OrderBy(t => t.CreatedAtUtc)
        .ToListAsync(ct);
    DemoQueryCounter.Naive();

    await redis.SetAsync(NaiveCache.TodosKey, JsonSerializer.SerializeToUtf8Bytes(items),
        new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10) }, ct);

    return Results.Ok(items);
});

todos.MapGet("/{id:guid}", async (Guid id, IFusionCache cache, IServiceScopeFactory scopes) =>
{
    var item = await cache.GetOrSetAsync($"todos:{id}", async ct =>
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TodoDbContext>();
        await Task.Delay(simulatedDbLatency, ct);
        return await db.Todos.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
    });

    return item is null ? Results.NotFound() : Results.Ok(item);
});

todos.MapPost("/", async (CreateTodoRequest request, TodoDbContext db, IFusionCache cache, IDistributedCache redis) =>
{
    var item = new TodoItem { Title = request.Title };
    db.Todos.Add(item);
    await db.SaveChangesAsync();
    await cache.RemoveAsync("todos:all");
    await redis.RemoveAsync(NaiveCache.TodosKey);

    return Results.Created($"/api/todos/{item.Id}", item);
});

todos.MapPut("/{id:guid}", async (Guid id, UpdateTodoRequest request, TodoDbContext db, IFusionCache cache, IDistributedCache redis) =>
{
    var item = await db.Todos.FindAsync(id);
    if (item is null)
    {
        return Results.NotFound();
    }

    item.Title = request.Title ?? item.Title;
    item.IsComplete = request.IsComplete ?? item.IsComplete;
    await db.SaveChangesAsync();

    await cache.RemoveAsync("todos:all");
    await cache.RemoveAsync($"todos:{id}");
    await redis.RemoveAsync(NaiveCache.TodosKey);

    return Results.Ok(item);
});

todos.MapDelete("/{id:guid}", async (Guid id, TodoDbContext db, IFusionCache cache, IDistributedCache redis) =>
{
    var item = await db.Todos.FindAsync(id);
    if (item is null)
    {
        return Results.NotFound();
    }

    db.Todos.Remove(item);
    await db.SaveChangesAsync();

    await cache.RemoveAsync("todos:all");
    await cache.RemoveAsync($"todos:{id}");
    await redis.RemoveAsync(NaiveCache.TodosKey);

    return Results.NoContent();
});

app.Run();

record CreateTodoRequest(string Title);
record UpdateTodoRequest(string? Title, bool? IsComplete);

internal static class NaiveCache
{
    public const string TodosKey = "naive:todos:all";
}
