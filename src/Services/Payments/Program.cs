using System.Net.Http.Headers;

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
    .Validate(o => o.SessionLifetime >= TimeSpan.FromMinutes(30) && o.SessionLifetime <= TimeSpan.FromHours(24),
        "Payments:SessionLifetime must be between 30 minutes and 24 hours, the provider's range.")
    .ValidateOnStart();
builder.Services.TryAddSingleton(TimeProvider.System);

builder.AddMessaging<PaymentsDbContext>(m => m
    .AddTopic(PaymentEvents.Topic, PaymentEvents.Partitions)
    .AddTopic(OrderEvents.Topic, OrderEvents.Partitions)
    .AddHandler<RefundRequested, RefundRequestedHandler>(OrderEvents.Topic));

builder.Services.AddScoped<PaymentService>();

// The provider client: FakePay's address and key from configuration, then the standard resilience pipeline (retry, circuit
// breaker, timeouts). Retrying a POST is safe because every provider call carries an idempotency key.
builder.Services.AddHttpClient<IPaymentProvider, FakePayProvider>((services, client) =>
    {
        var provider = services.GetRequiredService<IOptions<PaymentsOptions>>().Value.Provider;
        client.BaseAddress = new Uri(provider.BaseUrl);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", provider.ApiKey);
    })
    .AddStandardResilienceHandler();

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
