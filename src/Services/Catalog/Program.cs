using NewStore.ServiceDefaults;
using NewStore.ServiceDefaults.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Fail fast: a service without its database should not start and then fail on every request.
var connectionString = builder.Configuration.GetConnectionString("Catalog");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("Connection string 'ConnectionStrings:Catalog' is missing.");
}

builder.Services.AddNpgsqlDataSource(connectionString);
builder.Services.AddHealthChecks().AddPostgres(connectionString);

var app = builder.Build();

app.MapDefaultEndpoints();

app.Run();