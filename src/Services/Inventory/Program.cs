using Microsoft.Extensions.Options;

using NewStore.Contracts.Inventory;
using NewStore.Inventory;
using NewStore.Messaging;
using NewStore.ServiceDefaults;
using NewStore.ServiceDefaults.Data;
using NewStore.ServiceDefaults.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddPostgresDatabase<InventoryDbContext>("Inventory");

builder.Services.AddOptions<InventoryOptions>().BindConfiguration("Inventory");

// Every "now" in the service comes from here, so tests replace the clock instead of sleeping.
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddSingleton<InventoryMetrics>();
builder.Services.AddScoped<ReservationService>();
builder.Services.AddSingleton<SweeperHeartbeat>();
builder.Services.AddHostedService<ReservationExpiryService>();
builder.Services.AddHealthChecks()
    .AddCheck<SweeperHealthCheck>("reservation-sweeper", tags: [HealthCheckExtensions.LiveTag]);
builder.AddMessaging<InventoryDbContext>(m => m.AddTopic(EditionSnapshot.Topic, EditionSnapshot.Partitions));
builder.Services.AddSingleton<EditionSnapshotHeartbeat>();
builder.Services.AddHostedService<EditionSnapshotPublisher>();
builder.Services.AddHealthChecks()
    .AddCheck<EditionSnapshotHealthCheck>("edition-snapshots", tags: [HealthCheckExtensions.LiveTag]);
builder.Services.AddScoped<ConditionalUpdateStrategy>();
builder.Services.AddScoped<ConcurrencyTokenStrategy>();
builder.Services.AddScoped<IReserveStrategy>(services =>
    services.GetRequiredService<IOptions<InventoryOptions>>().Value.ReserveStrategy switch
    {
        ReserveStrategy.ConditionalUpdate => services.GetRequiredService<ConditionalUpdateStrategy>(),
        ReserveStrategy.ConcurrencyToken => services.GetRequiredService<ConcurrencyTokenStrategy>(),
        var other => throw new InvalidOperationException($"Unknown reserve strategy {other}."),
    });

var app = builder.Build();

// "migrate" and "seed" run as one-shot commands (ADR 0005); with no arguments the service runs.
if (await app.RunDatabaseCommandsAsync<InventoryDbContext>(args) is { } exitCode)
{
    return exitCode;
}

app.MapDefaultEndpoints();
app.MapEditionEndpoints();
app.MapReservationEndpoints();

app.Run();
return 0;
