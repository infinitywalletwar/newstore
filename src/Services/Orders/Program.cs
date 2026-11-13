using Microsoft.Extensions.DependencyInjection.Extensions;

using NewStore.Contracts.Orders;
using NewStore.Contracts.Payments;
using NewStore.Messaging;
using NewStore.Orders;
using NewStore.Orders.Clients;
using NewStore.Orders.Orders;
using NewStore.Orders.Saga;
using NewStore.ServiceDefaults;
using NewStore.ServiceDefaults.Data;
using NewStore.ServiceDefaults.HealthChecks;
using NewStore.ServiceDefaults.Security;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddPostgresDatabase<OrdersDbContext>("Orders");

// Buyers' tokens are validated here (the gateway signs them in); Orders' own client "orders" calls Inventory and Payments (ADR 0012).
builder.AddNewStoreAuth();
builder.AddServiceClient<InventoryClient>("Services:Inventory");
builder.AddServiceClient<PaymentsClient>("Services:Payments");

// The first buy used to wait for the OpenID metadata, the signing keys, Orders' own token and a connection to Inventory, and a burst of
// buyers all waited with it. Readiness stays unhealthy until one warm-up has fetched them (it never becomes a permanent dependency).
builder.AddReadinessWarmUp(async (services, ct) =>
{
    await WarmUpExtensions.WarmUpAuthAsync(services, ct);
    await services.GetRequiredService<InventoryClient>().WarmUpAsync(ct);
});

builder.Services.AddOptions<OrdersOptions>()
    .BindConfiguration(OrdersOptions.SectionName)
    .Validate(o => Uri.TryCreate(o.WebBaseUrl, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https",
        "Orders:WebBaseUrl must be an absolute http or https URL: the payment page sends the buyer back there.")
    .ValidateOnStart();
builder.Services.TryAddSingleton(TimeProvider.System);

// The checkout saga's inputs: Payments' three events. Its one output, RefundRequested, goes through the outbox.
builder.AddMessaging<OrdersDbContext>(m => m
    .AddTopic(OrderEvents.Topic, OrderEvents.Partitions)
    .AddTopic(PaymentEvents.Topic, PaymentEvents.Partitions)
    .AddHandler<PaymentSucceeded, PaymentSucceededHandler>(PaymentEvents.Topic)
    .AddHandler<PaymentFailed, PaymentFailedHandler>(PaymentEvents.Topic)
    .AddHandler<PaymentRefunded, PaymentRefundedHandler>(PaymentEvents.Topic));

var app = builder.Build();

// "migrate" runs as a one-shot command (ADR 0005); with no arguments the service runs.
if (await app.RunDatabaseCommandsAsync<OrdersDbContext>(args) is { } exitCode)
{
    return exitCode;
}

app.MapDefaultEndpoints();
app.UseAuthentication();
app.UseAuthorization();
app.MapOrderEndpoints();

app.Run();
return 0;
