using System.Diagnostics;
using System.Text;

using Confluent.Kafka;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace NewStore.Messaging.Tests;

/// <summary>
/// What one test owns: a fresh database with the tables, and a topic name no other test uses, so tests
/// don't see each other's rows or messages.
/// </summary>
internal sealed class Scenario
{
    private readonly KafkaServer _kafka;

    private Scenario(KafkaServer kafka, string connectionString, string topic)
    {
        _kafka = kafka;
        ConnectionString = connectionString;
        Topic = topic;
    }

    public string ConnectionString { get; }

    public string Topic { get; }

    public string BootstrapServers => _kafka.BootstrapServers;

    public static async Task<Scenario> CreateAsync(PostgresServer postgres, KafkaServer kafka, bool createTopic = true)
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync("things", ct);
        var scenario = new Scenario(kafka, connectionString, $"things-{Guid.CreateVersion7():N}");

        await using (var context = scenario.OpenContext())
        {
            await context.Database.EnsureCreatedAsync(ct);
        }

        if (createTopic)
        {
            await kafka.CreateTopicAsync(scenario.Topic, 1);
        }

        return scenario;
    }

    public ThingsDbContext OpenContext() =>
        new(new DbContextOptionsBuilder<ThingsDbContext>()
            .UseNpgsql(ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options);

    public async Task<IHost> StartHostAsync(
        string clientId = "things-service",
        Action<MessagingBuilder>? configure = null,
        Action<MessagingOptions>? options = null)
    {
        var host = MessagingHost.CreateBuilder(BootstrapServers, ConnectionString, clientId, configure, options).Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        return host;
    }

    /// <summary>Does what a request handler does: adds a business row and the event, then one SaveChanges.</summary>
    public static async Task CreateThingAsync(IHost host, ThingCreated created, string topic)
    {
        using var scope = host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ThingsDbContext>();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutbox>();

        context.Things.Add(new Thing { Id = created.ThingId, Name = created.Name });
        outbox.Enqueue(created, topic, created.ThingId.ToString());
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Reads <paramref name="topic"/> from the start until <paramref name="count"/> messages arrived or the time ran out.</summary>
    public async Task<List<ConsumeResult<string, string>>> ReadAsync(string topic, int count, TimeSpan timeout)
    {
        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = BootstrapServers,
            GroupId = $"test-reader-{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
        }).Build();
        consumer.Subscribe(topic);

        var results = new List<ConsumeResult<string, string>>();
        var deadline = Stopwatch.StartNew();
        while (results.Count < count && deadline.Elapsed < timeout)
        {
            var result = consumer.Consume(TimeSpan.FromMilliseconds(200));
            if (result is not null)
            {
                results.Add(result);
            }
        }

        return results;
    }

    public static string Header(ConsumeResult<string, string> result, string name) =>
        Encoding.UTF8.GetString(result.Message.Headers.GetLastBytes(name));

    /// <summary>Polls until the condition holds. Tests wait for a state, never for a fixed time.</summary>
    public static async Task EventuallyAsync(Func<Task<bool>> condition, TimeSpan timeout, string because)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < timeout)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        Assert.Fail($"Timed out after {timeout.TotalSeconds:F0} s waiting for: {because}");
    }
}
