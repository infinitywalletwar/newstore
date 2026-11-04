namespace NewStore.Catalog.Tests;

public class StartupTests
{
    [Fact]
    public void Startup_fails_when_connection_string_is_missing()
    {
        using var factory = new CatalogAppFactory { Settings = { ["ConnectionStrings:Catalog"] = "" } };

        var ex = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.Contains("ConnectionStrings:Catalog", ex.Message);
    }
}