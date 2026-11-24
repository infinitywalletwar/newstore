namespace NewStore.Catalog.Tests;

/// <summary>
/// The seeded Catalog on a Kafka container of its own. For a test class that publishes messages only its own app can handle:
/// every Catalog app in the assembly consumes the shared broker's topics from the start, and each such message costs another
/// app's one consumer <c>MaxHandlerAttempts</c> tries with a 1 s pause between them before the message is dead-lettered.
/// </summary>
public sealed class OwnKafkaCatalogAppFactory(PostgresServer postgres, KafkaServer kafka) : SeededCatalogAppFactory(postgres, kafka)
{
    public KafkaServer Kafka { get; } = new();

    protected override async ValueTask ConfigureSettingsAsync()
    {
        await Kafka.InitializeAsync();

        // Replaces the shared broker that SeededCatalogAppFactory set a moment ago; the app has not started yet.
        this.UseKafka(Kafka);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await Kafka.DisposeAsync();
    }
}
