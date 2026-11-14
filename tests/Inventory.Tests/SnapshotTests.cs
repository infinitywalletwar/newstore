using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using NewStore.Contracts.Inventory;
using NewStore.Messaging;
using NewStore.Messaging.Outbox;

using Npgsql;

namespace NewStore.Inventory.Tests;

/// <summary>
/// The snapshot pass (ADR 0011), called directly so the publisher loop never races it: the loop's timer runs on
/// the fake clock, which these tests never advance. The tests share one database, so each one first drains what an
/// earlier one left behind, and then counts only what its own steps change.
/// </summary>
public sealed class SnapshotTests(ConditionalUpdateFixture fixture) : IClassFixture<ConditionalUpdateFixture>, IAsyncLifetime
{
    private const int BigBatch = 1000;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await PassAsync(BigBatch);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task First_pass_sends_version_1_of_each_edition()
    {
        using var client = fixture.CreateClient();
        var first = await client.CreateEditionAsync(copies: 10);
        var second = await client.CreateEditionAsync(copies: 4);

        Assert.Equal(2, await PassAsync(BigBatch));

        foreach (var edition in new[] { first, second })
        {
            var row = Assert.Single(await OutboxRowsAsync(edition.Id));
            Assert.Equal(EditionSnapshot.Topic, row.Topic);
            Assert.Equal(edition.Id.ToString(), row.Key);
            Assert.Equal(typeof(EditionSnapshot).FullName, row.Type);

            var snapshot = JsonSerializer.Deserialize<EditionSnapshot>(row.Payload)!;
            Assert.Equal(edition.Id, snapshot.EditionId);
            Assert.Equal(edition.VideoId, snapshot.VideoId);
            Assert.Equal("Digital", snapshot.Format);
            Assert.Equal(edition.Price, snapshot.Price);
            Assert.Equal(edition.CopiesTotal, snapshot.CopiesTotal);
            Assert.Equal(edition.CopiesLeft, snapshot.CopiesLeft);
            Assert.Null(snapshot.EndedAt);
            Assert.Equal(1, snapshot.Version);
        }
    }

    [Fact]
    public async Task Pass_without_changes_sends_nothing()
    {
        using var client = fixture.CreateClient();
        await client.CreateEditionAsync();
        await PassAsync(BigBatch);
        var before = await OutboxCountAsync();

        Assert.Equal(0, await PassAsync(BigBatch));

        Assert.Equal(before, await OutboxCountAsync());
    }

    [Fact]
    public async Task Reserves_between_passes_make_one_snapshot()
    {
        using var client = fixture.CreateClient();
        var edition = await client.CreateEditionAsync(copies: 10);
        await PassAsync(BigBatch);

        for (var i = 0; i < 3; i++)
        {
            await client.ReserveOkAsync(edition.Id);
        }

        Assert.Equal(1, await PassAsync(BigBatch));

        var snapshot = await LastSnapshotAsync(edition.Id);
        Assert.Equal(2, snapshot.Version);
        Assert.Equal(edition.CopiesTotal - 3, snapshot.CopiesLeft);
    }

    [Fact]
    public async Task End_of_sale_is_in_the_snapshot()
    {
        using var client = fixture.CreateClient();
        var edition = await client.CreateEditionAsync(copies: 10);
        await PassAsync(BigBatch);

        Assert.True((await client.EndSaleAsync(edition.Id)).IsSuccessStatusCode);
        Assert.Equal(1, await PassAsync(BigBatch));

        var snapshot = await LastSnapshotAsync(edition.Id);
        Assert.NotNull(snapshot.EndedAt);
        Assert.Equal(0, snapshot.CopiesLeft);
        Assert.Equal(2, snapshot.Version);
    }

    [Fact]
    public async Task Pass_skips_while_another_holds_the_lock()
    {
        using var client = fixture.CreateClient();
        var edition = await client.CreateEditionAsync();
        var before = await OutboxCountAsync();

        await using (var other = new NpgsqlConnection(fixture.Settings["ConnectionStrings:Inventory"]))
        {
            await other.OpenAsync(Ct);
            await using var transaction = await other.BeginTransactionAsync(Ct);
            await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(11001)", other, transaction);
            await command.ExecuteNonQueryAsync(Ct);

            Assert.Null(await PassAsync(BigBatch));
            Assert.Equal(before, await OutboxCountAsync());

            await transaction.RollbackAsync(Ct);
        }

        Assert.Equal(1, await PassAsync(BigBatch));
        Assert.Single(await OutboxRowsAsync(edition.Id));
    }

    [Fact]
    public async Task Batch_size_limits_one_pass()
    {
        using var client = fixture.CreateClient();
        for (var i = 0; i < 3; i++)
        {
            await client.CreateEditionAsync();
        }

        Assert.Equal(2, await PassAsync(2));
        Assert.Equal(1, await PassAsync(2));
    }

    private async Task<int?> PassAsync(int batchSize)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        return await EditionSnapshots.PublishAsync(
            scope.ServiceProvider.GetRequiredService<InventoryDbContext>(),
            scope.ServiceProvider.GetRequiredService<IOutbox>(),
            fixture.Clock.GetUtcNowToMicrosecond(),
            batchSize,
            Ct);
    }

    private async Task<List<OutboxMessage>> OutboxRowsAsync(Guid editionId)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        return await db.Set<OutboxMessage>().Where(m => m.Key == editionId.ToString()).OrderBy(m => m.OccurredAt).ToListAsync(Ct);
    }

    private async Task<int> OutboxCountAsync()
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<InventoryDbContext>().Set<OutboxMessage>().CountAsync(Ct);
    }

    private async Task<EditionSnapshot> LastSnapshotAsync(Guid editionId) =>
        (await OutboxRowsAsync(editionId))
            .Select(r => JsonSerializer.Deserialize<EditionSnapshot>(r.Payload)!)
            .MaxBy(s => s.Version)!;
}
