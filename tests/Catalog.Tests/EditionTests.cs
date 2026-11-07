using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Confluent.Kafka;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using NewStore.Catalog.Editions;
using NewStore.Catalog.Videos;
using NewStore.Contracts.Inventory;
using NewStore.Messaging;
using NewStore.Messaging.Kafka;

namespace NewStore.Catalog.Tests;

public class EditionTests(SeededCatalogAppFactory factory) : IClassFixture<SeededCatalogAppFactory>
{
    private static EditionSnapshot Snapshot(
        Guid editionId, Guid videoId, long version, int copiesLeft = 10, decimal price = 9.99m, DateTimeOffset? endedAt = null) =>
        new()
        {
            EventId = Guid.NewGuid(),
            OccurredAt = DateTimeOffset.UtcNow,
            EditionId = editionId,
            VideoId = videoId,
            Format = "Dvd",
            Price = price,
            CopiesTotal = 10,
            CopiesLeft = copiesLeft,
            EndedAt = endedAt,
            Version = version,
        };

    private async Task HandleAsync(EditionSnapshot snapshot)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IIntegrationEventHandler<EditionSnapshot>>()
            .HandleAsync(snapshot, TestContext.Current.CancellationToken);
    }

    private async Task<EditionCopy?> FindAsync(Guid editionId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<CatalogDbContext>().Editions
            .AsNoTracking().SingleOrDefaultAsync(e => e.Id == editionId, TestContext.Current.CancellationToken);
    }

    /// <summary>The ids of the seeded videos, newest first.</summary>
    private async Task<Guid[]> VideoIdsAsync()
    {
        using var client = factory.CreateClient();
        var page = await client.GetFromJsonAsync<PagedResult<VideoSummary>>("/videos?pageSize=5", TestContext.Current.CancellationToken);
        return [.. page!.Items.Select(v => v.Id)];
    }

    [Fact]
    public async Task Handler_stores_the_first_snapshot()
    {
        var editionId = Guid.NewGuid();
        var videoId = Guid.NewGuid();
        var snapshot = Snapshot(editionId, videoId, version: 1, copiesLeft: 7, price: 12.50m);

        await HandleAsync(snapshot);

        var row = await FindAsync(editionId);
        Assert.NotNull(row);
        Assert.Equal(videoId, row.VideoId);
        Assert.Equal("Dvd", row.Format);
        Assert.Equal(12.50m, row.Price);
        Assert.Equal(10, row.CopiesTotal);
        Assert.Equal(7, row.CopiesLeft);
        Assert.Null(row.EndedAt);
        Assert.Equal(1, row.Version);
    }

    [Fact]
    public async Task Newer_snapshot_replaces_the_row()
    {
        var editionId = Guid.NewGuid();
        var videoId = Guid.NewGuid();
        await HandleAsync(Snapshot(editionId, videoId, version: 1, copiesLeft: 10));

        await HandleAsync(Snapshot(editionId, videoId, version: 2, copiesLeft: 4));

        var row = await FindAsync(editionId);
        Assert.NotNull(row);
        Assert.Equal(2, row.Version);
        Assert.Equal(4, row.CopiesLeft);
    }

    [Fact]
    public async Task Older_snapshot_does_not_overwrite_a_newer_one()
    {
        var editionId = Guid.NewGuid();
        var videoId = Guid.NewGuid();
        await HandleAsync(Snapshot(editionId, videoId, version: 3, copiesLeft: 2));

        await HandleAsync(Snapshot(editionId, videoId, version: 2, copiesLeft: 9));

        var row = await FindAsync(editionId);
        Assert.NotNull(row);
        Assert.Equal(3, row.Version);
        Assert.Equal(2, row.CopiesLeft);
    }

    [Fact]
    public async Task Editions_of_a_video_newest_state()
    {
        var videoId = (await VideoIdsAsync())[0];
        var cheap = Guid.NewGuid();
        var dear = Guid.NewGuid();
        var ended = Guid.NewGuid();
        await HandleAsync(Snapshot(dear, videoId, version: 1, copiesLeft: 5, price: 30m));
        await HandleAsync(Snapshot(cheap, videoId, version: 1, copiesLeft: 10, price: 10m));
        await HandleAsync(Snapshot(cheap, videoId, version: 2, copiesLeft: 8, price: 10m));
        await HandleAsync(Snapshot(ended, videoId, version: 1, copiesLeft: 0, price: 50m, endedAt: DateTimeOffset.UtcNow));
        using var client = factory.CreateClient();

        var editions = await client.GetFromJsonAsync<EditionView[]>($"/videos/{videoId}/editions", TestContext.Current.CancellationToken);

        Assert.NotNull(editions);
        Assert.Equal([cheap, dear, ended], editions.Select(e => e.Id));
        Assert.Equal([8, 5, 0], editions.Select(e => e.CopiesLeft));
        Assert.Equal([true, true, false], editions.Select(e => e.OnSale));
    }

    [Fact]
    public async Task Editions_of_an_unknown_video_returns_404()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/videos/{Guid.NewGuid()}/editions", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("Video not found.", problem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Video_without_editions_returns_an_empty_list()
    {
        var videoId = (await VideoIdsAsync())[1];
        using var client = factory.CreateClient();

        var editions = await client.GetFromJsonAsync<EditionView[]>($"/videos/{videoId}/editions", TestContext.Current.CancellationToken);

        Assert.NotNull(editions);
        Assert.Empty(editions);
    }

    [Fact]
    public async Task Snapshot_from_Kafka_reaches_the_endpoint()
    {
        var ct = TestContext.Current.CancellationToken;
        var videoId = (await VideoIdsAsync())[2];
        var editionId = Guid.NewGuid();
        using var producer = new ProducerBuilder<string, string>(new ProducerConfig
        {
            BootstrapServers = factory.Settings["Messaging:BootstrapServers"],
        }).Build();
        using var client = factory.CreateClient();

        // Shaped like OutboxRelay's messages: key = edition id, value = the record's JSON, three headers.
        Message<string, string> Message(EditionSnapshot snapshot, Guid messageId) => new()
        {
            Key = editionId.ToString(),
            Value = JsonSerializer.Serialize(snapshot),
            Headers = new Headers
            {
                { MessageHeaders.MessageId, Encoding.UTF8.GetBytes(messageId.ToString()) },
                { MessageHeaders.Type, Encoding.UTF8.GetBytes(typeof(EditionSnapshot).FullName!) },
                { MessageHeaders.OccurredAt, Encoding.UTF8.GetBytes(snapshot.OccurredAt.ToString("O")) },
            },
        };

        var current = Message(Snapshot(editionId, videoId, version: 2, copiesLeft: 6), Guid.NewGuid());
        await producer.ProduceAsync(EditionSnapshot.Topic, current, ct);

        EditionView? arrived = null;
        await PollUntilAsync(async () =>
        {
            arrived = (await client.GetFromJsonAsync<EditionView[]>($"/videos/{videoId}/editions", ct))!
                .SingleOrDefault(e => e.Id == editionId);
            return arrived is not null;
        });
        Assert.Equal(6, arrived!.CopiesLeft);
        var before = await FindAsync(editionId);

        // The same message again (the inbox drops it), then an older version with a new message id (the version
        // guard drops it). They share a key, so one partition handles them in this order.
        var staleId = Guid.NewGuid();
        await producer.ProduceAsync(EditionSnapshot.Topic, current, ct);
        await producer.ProduceAsync(EditionSnapshot.Topic, Message(Snapshot(editionId, videoId, version: 1, copiesLeft: 10), staleId), ct);
        await PollUntilAsync(() => InboxHasAsync(staleId));

        var after = await FindAsync(editionId);
        Assert.Equal(2, after!.Version);
        Assert.Equal(6, after.CopiesLeft);
        Assert.Equal(before!.UpdatedAt, after.UpdatedAt);
    }

    private async Task<bool> InboxHasAsync(Guid messageId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var count = await db.Database
            .SqlQuery<long>($"SELECT count(*) AS \"Value\" FROM inbox_messages WHERE message_id = {messageId}")
            .SingleAsync(TestContext.Current.CancellationToken);
        return count > 0;
    }

    private static async Task PollUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        }

        Assert.Fail("The condition did not hold within 15 seconds.");
    }
}
