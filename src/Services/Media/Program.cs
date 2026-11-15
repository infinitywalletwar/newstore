using Microsoft.Extensions.DependencyInjection.Extensions;

using NewStore.Contracts.Inventory;
using NewStore.Media;
using NewStore.Messaging;
using NewStore.ServiceDefaults;
using NewStore.ServiceDefaults.Data;
using NewStore.ServiceDefaults.HealthChecks;
using NewStore.ServiceDefaults.Security;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddPostgresDatabase<MediaDbContext>("Media");
var role = builder.AddMediaOptions();    // binds Media:*, validates StorageOptions on start, returns Media:Role
builder.AddMediaStorage();               // MediaStorage, StorageSetup, the Degraded "storage" check
builder.Services.TryAddSingleton(TimeProvider.System);

// Media consumes CopyAssigned (Task 6) and publishes VideoPublished (Task 5), both through the outbox and inbox.
builder.AddMessaging<MediaDbContext>(m =>
{
    m.AddTopic(CopyAssigned.Topic, CopyAssigned.Partitions);
    // Task 5: m.AddTopic(VideoPublished.Topic, VideoPublished.Partitions);
    // Task 6: if (role is MediaRole.All or MediaRole.Api) m.AddHandler<CopyAssigned, CopyAssignedHandler>(CopyAssigned.Topic);
});

if (role is MediaRole.All or MediaRole.Api)
{
    // Creators' tokens are validated here (the gateway signs them in); Media's own client "media" calls Catalog (ADR 0012).
    builder.AddNewStoreAuth();
    builder.AddServiceClient<CatalogClient>("Services:Catalog");
    builder.AddReadinessWarmUp(WarmUpExtensions.WarmUpAuthAsync);
    builder.AddUploads();                // UploadService, UploadExpiryService
    // Task 6: builder.AddDownloads();
}

// Task 5: builder.AddProcessing(runWorker: role is MediaRole.All or MediaRole.Worker);
builder.Services.AddScoped<IDatabaseSeeder<MediaDbContext>, MediaSeeder>();   // StorageSetup.EnsureAsync; Task 5 adds the sample

var app = builder.Build();

// "migrate" and "seed" run as one-shot commands (ADR 0005); with no arguments the service runs.
if (await app.RunDatabaseCommandsAsync<MediaDbContext>(args) is { } exitCode)
{
    return exitCode;
}

app.MapDefaultEndpoints();
if (role is MediaRole.All or MediaRole.Api)
{
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapUploadEndpoints();
    // Task 6: app.MapDownloadEndpoints();
}

app.Run();
return 0;
