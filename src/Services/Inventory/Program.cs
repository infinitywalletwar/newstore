using NewStore.Inventory;
using NewStore.ServiceDefaults;
using NewStore.ServiceDefaults.Data;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddPostgresDatabase<InventoryDbContext>("Inventory");

builder.Services.AddOptions<InventoryOptions>().BindConfiguration("Inventory");

// Every "now" in the service comes from here, so tests replace the clock instead of sleeping.
builder.Services.AddSingleton(TimeProvider.System);

var app = builder.Build();

// "migrate" and "seed" run as one-shot commands (ADR 0005); with no arguments the service runs.
if (await app.RunDatabaseCommandsAsync<InventoryDbContext>(args) is { } exitCode)
{
    return exitCode;
}

app.MapDefaultEndpoints();
app.MapEditionEndpoints();

app.Run();
return 0;
