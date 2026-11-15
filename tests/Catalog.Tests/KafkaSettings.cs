namespace NewStore.Catalog.Tests;

internal static class KafkaSettings
{
    /// <summary>
    /// Points the app at the test broker. <c>AddMessaging</c> validates <c>Messaging:BootstrapServers</c> on start, and
    /// the consumer's group id is the client id (<c>KafkaConsumerService</c>: <c>GroupId = settings.ClientId</c>).
    /// Test classes run in parallel, so each app gets its own client id: with a shared one, two apps would be one
    /// group and split the topic's partitions between them.
    /// </summary>
    public static void UseKafka(this NewStoreAppFactory<Program> factory, KafkaServer kafka)
    {
        factory.Settings["Messaging:BootstrapServers"] = kafka.BootstrapServers;
        factory.Settings["Messaging:ClientId"] = $"catalog-tests-{Guid.NewGuid():N}";
        factory.UseMediaBaseUrl();
    }

    public const string MediaBaseUrl = "http://media.test/newstore-media";

    /// <summary>Catalog does not start without <c>Catalog:MediaBaseUrl</c>, so every test app sets it.</summary>
    public static void UseMediaBaseUrl(this NewStoreAppFactory<Program> factory) =>
        factory.Settings["Catalog:MediaBaseUrl"] = MediaBaseUrl;
}
