using ChaosToDo.Api.Data;
using ChaosToDo.Api.Models;
using Microsoft.EntityFrameworkCore;
using ZiggyCreatures.Caching.Fusion;
using ZiggyCreatures.Caching.Fusion.Serialization.SystemTextJson;

var builder = WebApplication.CreateBuilder(args);

// Aspire service defaults: OpenTelemetry, health checks, service discovery, resilience.
builder.AddServiceDefaults();

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

// A deliberately slow "downstream" simulation so a cold cache / cache flush is
// clearly visible during the demo (Cache Stampede scenario flushes Redis).
var simulatedDbLatency = builder.Configuration.GetValue("Demo:SimulatedDbLatencyMs", 800);

builder.Services.AddFusionCache()
    .WithDefaultEntryOptions(options => options
        .SetDuration(TimeSpan.FromSeconds(30))
        .SetFailSafe(true, TimeSpan.FromHours(2))
        .SetFactoryTimeouts(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(5)))
    .WithSerializer(new FusionCacheSystemTextJsonSerializer())
    // Reuses the IDistributedCache registered above by AddStackExchangeRedisCache (L2):
    // this is what turns a Redis flush into a real, visible cold-cache event.
    .WithRegisteredDistributedCache()
    .WithStackExchangeRedisBackplane(options => options.Configuration = redisConnectionString);

builder.Services.AddOpenApi();

var app = builder.Build();

app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

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
// right after a Cache Stampede run (Redis flushed) every concurrent caller
// collapses onto a single factory execution instead of hammering the database.
todos.MapGet("/", async (IFusionCache cache, TodoDbContext db) =>
{
    var items = await cache.GetOrSetAsync("todos:all", async ct =>
    {
        await Task.Delay(simulatedDbLatency, ct);
        return await db.Todos.AsNoTracking().OrderBy(t => t.CreatedAtUtc).ToListAsync(ct);
    });

    return Results.Ok(items);
});

// GET /api/todos/nocache — same query, no FusionCache in front. Use this
// endpoint under load *before* introducing FusionCache to show the stampede
// hitting Azure SQL directly, then switch back to "/" to show the fix.
todos.MapGet("/nocache", async (TodoDbContext db, CancellationToken ct) =>
{
    await Task.Delay(simulatedDbLatency, ct);
    var items = await db.Todos.AsNoTracking().OrderBy(t => t.CreatedAtUtc).ToListAsync(ct);
    return Results.Ok(items);
});

todos.MapGet("/{id:guid}", async (Guid id, IFusionCache cache, TodoDbContext db) =>
{
    var item = await cache.GetOrSetAsync($"todos:{id}", async ct =>
    {
        await Task.Delay(simulatedDbLatency, ct);
        return await db.Todos.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
    });

    return item is null ? Results.NotFound() : Results.Ok(item);
});

todos.MapPost("/", async (CreateTodoRequest request, TodoDbContext db, IFusionCache cache) =>
{
    var item = new TodoItem { Title = request.Title };
    db.Todos.Add(item);
    await db.SaveChangesAsync();
    await cache.RemoveAsync("todos:all");

    return Results.Created($"/api/todos/{item.Id}", item);
});

todos.MapPut("/{id:guid}", async (Guid id, UpdateTodoRequest request, TodoDbContext db, IFusionCache cache) =>
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

    return Results.Ok(item);
});

todos.MapDelete("/{id:guid}", async (Guid id, TodoDbContext db, IFusionCache cache) =>
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

    return Results.NoContent();
});

app.Run();

record CreateTodoRequest(string Title);
record UpdateTodoRequest(string? Title, bool? IsComplete);
