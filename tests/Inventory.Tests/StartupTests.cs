namespace NewStore.Inventory.Tests;

public class StartupTests
{
    [Fact]
    public void Startup_fails_when_connection_string_is_missing()
    {
        using var factory = new NewStoreAppFactory<Program>();

        var ex = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.Contains("ConnectionStrings:Inventory", ex.Message);
    }
}
