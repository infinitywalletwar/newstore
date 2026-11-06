namespace NewStore.Catalog.Tests;

public class StartupTests
{
    // The Testing environment leaves the key absent, so test both shapes: absent (null) and empty.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Startup_fails_when_connection_string_is_missing_or_empty(string? value)
    {
        using var factory = new NewStoreAppFactory<Program>();
        if (value is not null)
        {
            factory.Settings["ConnectionStrings:Catalog"] = value;
        }

        var ex = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.Contains("ConnectionStrings:Catalog", ex.Message);
    }
}
