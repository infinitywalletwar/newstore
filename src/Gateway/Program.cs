using NewStore.Gateway.Auth;
using NewStore.Gateway.Proxy;
using NewStore.Gateway.Redis;
using NewStore.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddGatewayRedis();
builder.AddGatewayAuth();
builder.AddGatewayProxy();

var app = builder.Build();

app.MapDefaultEndpoints();
app.UseStaticFiles();
app.UseAuthentication();
app.UseCsrfHeader();
app.UseAuthorization();

app.MapAuthEndpoints();
app.MapReverseProxy();
// A path under /api that no route lists is a 404 problem, never the app's index.html.
app.Map("/api/{**rest}", () => Problems.NotFound());
app.Map("/health/{**rest}", () => Problems.NotFound());
app.MapFallbackToFile("index.html");

app.Run();
