using Microsoft.Extensions.Diagnostics.HealthChecks;

using NewStore.ServiceDefaults.Data;

namespace NewStore.Media;

public static class MediaExtensions
{
    private const int ProbeBudgetSeconds = 3;

    /// <summary><see cref="MediaStorage"/>, <see cref="StorageSetup"/> and the <c>storage</c> readiness check, which is Degraded, never Unhealthy.</summary>
    public static IHostApplicationBuilder AddMediaStorage(this IHostApplicationBuilder builder)
    {
        builder.Services.AddSingleton<MediaStorage>();
        builder.Services.AddSingleton<StorageSetup>();
        builder.Services.AddHealthChecks().Add(new HealthCheckRegistration(
            "storage",
            services => new StorageHealthCheck(services.GetRequiredService<MediaStorage>()),
            failureStatus: HealthStatus.Degraded,
            tags: null,
            timeout: TimeSpan.FromSeconds(ProbeBudgetSeconds)));
        return builder;
    }

    /// <summary>The upload endpoints' service.</summary>
    public static IHostApplicationBuilder AddUploads(this IHostApplicationBuilder builder)
    {
        builder.Services.AddScoped<UploadService>();
        return builder;
    }
}

/// <summary>Seeds Media: the bucket's settings. Task 5 adds the sample video.</summary>
public sealed class MediaSeeder(StorageSetup storage) : IDatabaseSeeder<MediaDbContext>
{
    public Task SeedAsync(MediaDbContext db, CancellationToken cancellationToken) => storage.EnsureAsync(cancellationToken);
}
