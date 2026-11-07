using Microsoft.Extensions.DependencyInjection.Extensions;

using NewStore.Catalog;
using NewStore.Catalog.Caching;
using NewStore.Catalog.Channels;
using NewStore.Catalog.Editions;
using NewStore.Catalog.Seed;
using NewStore.Catalog.Videos;
using NewStore.Contracts.Inventory;
using NewStore.Messaging;
using NewStore.ServiceDefaults;
using NewStore.ServiceDefaults.Data;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddPostgresDatabase<CatalogDbContext>("Catalog");
builder.Services.TryAddSingleton(TimeProvider.System);
builder.AddMessaging<CatalogDbContext>(m => m
    .AddTopic(EditionSnapshot.Topic, EditionSnapshot.Partitions)
    .AddHandler<EditionSnapshot, EditionSnapshotHandler>(EditionSnapshot.Topic));
builder.AddCatalogCache();
builder.Services.AddScoped<IDatabaseSeeder<CatalogDbContext>, CatalogSeeder>();

var app = builder.Build();

// "migrate" and "seed" run as one-shot commands (ADR 0005); with no arguments the service runs.
if (await app.RunDatabaseCommandsAsync<CatalogDbContext>(args) is { } exitCode)
{
    return exitCode;
}

app.MapDefaultEndpoints();
app.MapVideoEndpoints();
app.MapChannelEndpoints();
app.MapEditionEndpoints();

app.Run();
return 0;
