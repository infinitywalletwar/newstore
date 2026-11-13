using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

using NewStore.Orders.Orders;

using Npgsql;

namespace NewStore.Orders.Tests;

/// <summary>
/// Makes the next saves of one order lose the <c>xmin</c> check, as a parallel request does: just before each of those saves it
/// updates the same row on another connection, so the save finds a newer row version and EF throws
/// <see cref="DbUpdateConcurrencyException"/>. On the last of the lost saves it can also store the deadline the save is about to
/// write, as a parallel pay that got there first would have done.
/// </summary>
public sealed class ConflictInjector : SaveChangesInterceptor
{
    /// <summary>The database's connection string with its password: the context's own <c>GetConnectionString()</c> leaves the password out.</summary>
    public string ConnectionString { get; set; } = "";

    private Guid _orderId;
    private int _losses;
    private bool _storeDeadlineOnLastLoss;

    /// <summary>Makes the next <paramref name="losses"/> saves of <paramref name="orderId"/> lose.</summary>
    public void LoseNext(Guid orderId, int losses, bool storeDeadlineOnLastLoss)
    {
        _orderId = orderId;
        _storeDeadlineOnLastLoss = storeDeadlineOnLastLoss;
        Volatile.Write(ref _losses, losses);
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        var order = eventData.Context?.ChangeTracker.Entries<Order>()
            .FirstOrDefault(e => e.State == EntityState.Modified && e.Entity.Id == _orderId)?.Entity;
        var left = order is null ? -1 : Interlocked.Decrement(ref _losses);
        if (order is not null && left >= 0)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync(cancellationToken);
            var last = left == 0 && _storeDeadlineOnLastLoss;
            await using var command = new NpgsqlCommand(
                last
                    ? "UPDATE orders SET updated_at = clock_timestamp(), reserved_until = @until WHERE id = @id"
                    : "UPDATE orders SET updated_at = clock_timestamp() WHERE id = @id",
                connection);
            command.Parameters.AddWithValue("id", order.Id);
            if (last)
            {
                command.Parameters.AddWithValue("until", order.ReservedUntil);
            }

            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        return result;
    }
}
