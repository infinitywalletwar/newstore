using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using NewStore.ServiceDefaults;

namespace NewStore.Security.Tests;

public class ListenBacklogTests
{
    [Fact]
    public void Every_service_listens_with_a_backlog_that_survives_a_burst()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddServiceDefaults();
        using var host = builder.Build();

        // Kestrel's default is 512 pending connections; a burst of a thousand buyers overflowed it in the load test.
        Assert.Equal(4096, host.Services.GetRequiredService<IOptions<SocketTransportOptions>>().Value.Backlog);
    }
}
