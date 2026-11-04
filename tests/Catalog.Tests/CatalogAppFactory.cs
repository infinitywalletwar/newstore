using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace NewStore.Catalog.Tests;

/// <summary>Starts Catalog in memory. Each entry in <see cref="Settings"/> overrides configuration.</summary>
public class CatalogAppFactory : WebApplicationFactory<Program>
{
    public Dictionary<string, string?> Settings { get; } = [];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        foreach (var (key, value) in Settings)
        {
            builder.UseSetting(key, value);
        }
    }
}