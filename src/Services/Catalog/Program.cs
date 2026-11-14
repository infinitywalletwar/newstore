using Microsoft.Extensions.DependencyInjection.Extensions;

using NewStore.Catalog;
using NewStore.Catalog.Caching;
using NewStore.Catalog.Channels;
using NewStore.Catalog.Editions;
using NewStore.Catalog.Seed;
using NewStore.Catalog.Videos;
using NewStore.Contracts.Inventory;
using NewStore.Contracts.Media;
using NewStore.Messaging;
using NewStore.ServiceDefaults;
using NewStore.ServiceDefaults.Data;
using NewStore.ServiceDefaults.Security;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Reads stay anonymous. Opening a channel or adding a video needs a signed-in user; the owner lookup is for the clients "inventory" and "media".
builder.AddNewStoreAuth().AddClientsPolicy("inventory", "media");
builder.Services.AddOptions<CatalogOptions>()
    .BindConfiguration(CatalogOptions.SectionName)
    // Once, here: every URL is the base, a slash and a path, so a trailing slash would double it.
    .PostConfigure(o => o.MediaBaseUrl = o.MediaBaseUrl.TrimEnd('/'))
    .Validate(o => Uri.TryCreate(o.MediaBaseUrl, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https",
        "Catalog:MediaBaseUrl must be an absolute http or https URL: every playback and thumbnail URL starts with it.")
    .ValidateOnStart();
builder.AddPostgresDatabase<CatalogDbContext>("Catalog");
builder.Services.TryAddSingleton(TimeProvider.System);
builder.AddMessaging<CatalogDbContext>(m => m
    .AddTopic(EditionSnapshot.Topic, EditionSnapshot.Partitions)
    .AddHandler<EditionSnapshot, EditionSnapshotHandler>(EditionSnapshot.Topic)
    .AddTopic(VideoPublished.Topic, VideoPublished.Partitions)
    .AddHandler<VideoPublished, VideoPublishedHandler>(VideoPublished.Topic));
builder.AddCatalogCache();
builder.Services.AddScoped<IDatabaseSeeder<CatalogDbContext>, CatalogSeeder>();

var app = builder.Build();

// "migrate" and "seed" run as one-shot commands (ADR 0005); with no arguments the service runs.
if (await app.RunDatabaseCommandsAsync<CatalogDbContext>(args) is { } exitCode)
{
    return exitCode;
}

app.MapDefaultEndpoints();
app.UseAuthentication();
app.UseAuthorization();
app.MapVideoEndpoints();
app.MapChannelEndpoints();
app.MapEditionEndpoints();

app.Run();
return 0;
