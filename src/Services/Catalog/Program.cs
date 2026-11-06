using NewStore.Catalog;
using NewStore.ServiceDefaults;
using NewStore.ServiceDefaults.Data;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddPostgresDatabase<CatalogDbContext>("Catalog");

var app = builder.Build();

// "migrate" and "seed" run as one-shot commands (ADR 0005); with no arguments the service runs.
if (await app.RunDatabaseCommandsAsync<CatalogDbContext>(args) is { } exitCode)
{
    return exitCode;
}

app.MapDefaultEndpoints();

app.Run();
return 0;
