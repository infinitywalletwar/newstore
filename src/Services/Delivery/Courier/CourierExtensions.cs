namespace NewStore.Delivery.Courier;

public static class CourierExtensions
{
    public static IHostApplicationBuilder AddCourier(this IHostApplicationBuilder builder, bool runCourier)
    {
        builder.Services.AddScoped<CourierLeases>();
        return builder;
    }
}
