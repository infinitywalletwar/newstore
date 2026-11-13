using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

using NewStore.Contracts.Orders;
using NewStore.Contracts.Payments;
using NewStore.Messaging;
using NewStore.Payments;
using NewStore.Payments.Payments;
using NewStore.Payments.Provider;
using NewStore.Payments.Refunds;
using NewStore.ServiceDefaults;
using NewStore.ServiceDefaults.Data;
using NewStore.ServiceDefaults.Security;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddPostgresDatabase<PaymentsDbContext>("Payments");

// Tokens are validated here; each endpoint names the caller it accepts.
builder.AddNewStoreAuth().AddClientPolicy("orders");

builder.Services.AddOptions<PaymentsOptions>()
    .BindConfiguration(PaymentsOptions.SectionName)
    .Validate(o => Uri.IsWellFormedUriString(o.Provider.BaseUrl, UriKind.Absolute)
            && !string.IsNullOrWhiteSpace(o.Provider.ApiKey)
            && !string.IsNullOrWhiteSpace(o.Provider.WebhookSecret),
        "Payments:Provider:BaseUrl, ApiKey and WebhookSecret are required.")
    .Validate(o => o.Provider.SessionAttemptTimeout > TimeSpan.Zero
            && o.Provider.SessionTotalTimeout > o.Provider.SessionAttemptTimeout
            && o.Provider.SessionTotalTimeout <= TimeSpan.FromSeconds(8),
        "Payments:Provider:SessionAttemptTimeout must be positive and below SessionTotalTimeout, which must be at most 8 s: the row lock is held that long, and the caller gives up after 10 s.")
    .Validate(o => o.SessionLifetime >= TimeSpan.FromMinutes(30) && o.SessionLifetime <= TimeSpan.FromHours(24),
        "Payments:SessionLifetime must be between 30 minutes and 24 hours, the provider's range.")
    .ValidateOnStart();
builder.Services.TryAddSingleton(TimeProvider.System);

builder.AddMessaging<PaymentsDbContext>(m => m
    .AddTopic(PaymentEvents.Topic, PaymentEvents.Partitions)
    .AddTopic(OrderEvents.Topic, OrderEvents.Partitions)
    .AddHandler<RefundRequested, RefundRequestedHandler>(OrderEvents.Topic));

builder.Services.AddScoped<PaymentService>();

// The provider's two clients, each with its own resilience pipeline.
builder.Services.AddFakePayProvider();

var app = builder.Build();

// "migrate" runs as a one-shot command (ADR 0005); with no arguments the service runs.
if (await app.RunDatabaseCommandsAsync<PaymentsDbContext>(args) is { } exitCode)
{
    return exitCode;
}

app.MapDefaultEndpoints();
app.UseAuthentication();
app.UseAuthorization();
app.MapPaymentEndpoints();
app.MapWebhookEndpoint();

app.Run();
return 0;
