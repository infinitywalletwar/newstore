namespace NewStore.Delivery.Courier;

public static class CourierExtensions
{
    /// <summary>
    /// <see cref="CourierLeases"/> (scoped) and <see cref="CourierRunner"/> (singleton) in every role, so a test can drive a runner by
    /// hand; <see cref="CourierService"/>, the timer that ticks the runner, only when <paramref name="runCourier"/> is true.
    /// </summary>
    public static IHostApplicationBuilder AddCourier(this IHostApplicationBuilder builder, bool runCourier)
    {
        builder.Services.AddScoped<CourierLeases>();
        builder.Services.AddSingleton<CourierRunner>();
        if (runCourier)
        {
            builder.Services.AddHostedService<CourierService>();
        }

        return builder;
    }
}
