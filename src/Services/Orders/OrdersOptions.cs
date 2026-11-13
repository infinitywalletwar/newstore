namespace NewStore.Orders;

public sealed class OrdersOptions
{
    public const string SectionName = "Orders";

    /// <summary>Where the buyer's browser reaches the web app. The payment page sends the buyer back to <c>{WebBaseUrl}/orders/{id}</c>.</summary>
    public string WebBaseUrl { get; set; } = "";
}
