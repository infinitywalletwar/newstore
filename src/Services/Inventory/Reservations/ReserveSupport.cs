using Microsoft.EntityFrameworkCore;

using Npgsql;

namespace NewStore.Inventory;

internal static class ReserveSupport
{
    /// <summary>
    /// True when the insert broke the partial unique index on (edition, buyer): the buyer already holds a copy.
    /// Reservations has no other unique constraint a reserve can break.
    /// </summary>
    public static bool IsAlreadyHolding(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    public static Reservation NewPending(Guid editionId, Guid buyerId, TimeProvider time, InventoryOptions options)
    {
        var now = time.GetUtcNow();
        return new Reservation
        {
            Id = Guid.CreateVersion7(),
            EditionId = editionId,
            BuyerId = buyerId,
            Status = ReservationStatus.Pending,
            ReservedAt = now,
            ExpiresAt = now + options.ReservationTtl,
        };
    }
}
