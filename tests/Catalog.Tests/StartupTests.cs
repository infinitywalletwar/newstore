namespace NewStore.Catalog.Tests;

public class StartupTests(KafkaServer kafka)
{
    // The Testing environment leaves the key absent, so test both shapes: absent (null) and empty.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Startup_fails_when_connection_string_is_missing_or_empty(string? value)
    {
        using var factory = new NewStoreAppFactory<Program>();
        factory.UseMediaBaseUrl();
        if (value is not null)
        {
            factory.Settings["ConnectionStrings:Catalog"] = value;
        }

        var ex = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.Contains("ConnectionStrings:Catalog", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/relative")]
    public void Startup_fails_when_the_media_base_url_is_missing_or_relative(string value)
    {
        using var factory = new NewStoreAppFactory<Program>
        {
            Settings = { ["ConnectionStrings:Catalog"] = "Host=127.0.0.1;Port=1;Database=catalog;Username=catalog;Password=catalog;Timeout=2" },
        };
        factory.UseKafka(kafka);
        factory.Settings["Catalog:MediaBaseUrl"] = value;

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("Catalog:MediaBaseUrl must be an absolute http or https URL", ex.ToString());
    }
}
