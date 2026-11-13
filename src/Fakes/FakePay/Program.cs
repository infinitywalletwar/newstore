using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

using NewStore.FakePay;
using NewStore.FakePay.Sessions;
using NewStore.FakePay.Webhooks;
using NewStore.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddOptions<FakePayOptions>()
    .BindConfiguration(FakePayOptions.SectionName)
    .Validate(o => !string.IsNullOrWhiteSpace(o.ApiKey)
            && !string.IsNullOrWhiteSpace(o.WebhookSecret)
            && Uri.IsWellFormedUriString(o.WebhookUrl, UriKind.Absolute)
            && Uri.IsWellFormedUriString(o.PublicBaseUrl, UriKind.Absolute),
        "FakePay:ApiKey, WebhookSecret, WebhookUrl and PublicBaseUrl are required.")
    .ValidateOnStart();

// Every "now" comes from here, so tests move the clock instead of sleeping.
builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddHttpClient<WebhookClient>(client => client.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddSingleton<WebhookSender>();
builder.Services.AddHostedService(services => services.GetRequiredService<WebhookSender>());
builder.Services.AddHostedService<ExpirySweeper>();

var app = builder.Build();

app.MapDefaultEndpoints();
app.UseApiKey();
app.MapSessionEndpoints(app.Services.GetRequiredService<IOptions<FakePayOptions>>().Value);

app.Run();
