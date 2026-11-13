using Microsoft.Extensions.DependencyInjection.Extensions;

using NewStore.Contracts.Orders;
using NewStore.Contracts.Payments;
using NewStore.Messaging;
using NewStore.Payments;
using NewStore.ServiceDefaults;
using NewStore.ServiceDefaults.Data;
using NewStore.ServiceDefaults.Security;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddPostgresDatabase<PaymentsDbContext>("Payments");

// Tokens are validated here; each endpoint names the caller it accepts.
builder.AddNewStoreAuth().AddClientPolicy("orders");

builder.Services.AddOptions<PaymentsOptions>().BindConfiguration(PaymentsOptions.SectionName);
builder.Services.TryAddSingleton(TimeProvider.System);

builder.AddMessaging<PaymentsDbContext>(m => m
    .AddTopic(PaymentEvents.Topic, PaymentEvents.Partitions)
    .AddTopic(OrderEvents.Topic, OrderEvents.Partitions));

var app = builder.Build();

// "migrate" runs as a one-shot command (ADR 0005); with no arguments the service runs.
if (await app.RunDatabaseCommandsAsync<PaymentsDbContext>(args) is { } exitCode)
{
    return exitCode;
}

app.MapDefaultEndpoints();
app.UseAuthentication();
app.UseAuthorization();

app.Run();
return 0;
