using Microsoft.EntityFrameworkCore;

using NewStore.Delivery.Shipments;
using NewStore.Messaging;

namespace NewStore.Delivery;

public class DeliveryDbContext(DbContextOptions<DeliveryDbContext> options) : DbContext(options)
{
    public DbSet<Shipment> Shipments => Set<Shipment>();
    public DbSet<ShipmentPosition> ShipmentPositions => Set<ShipmentPosition>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Shipment>(shipment =>
        {
            shipment.Property(s => s.Id).ValueGeneratedNever();
            shipment.Property(s => s.Address).HasMaxLength(200);
            shipment.Property(s => s.WarehouseName).HasMaxLength(100);
            shipment.Property(s => s.Status).HasConversion<string>().HasMaxLength(16);
            shipment.Property(s => s.CourierLockedBy).HasMaxLength(100);

            // One shipment per order. The handler's INSERT ... ON CONFLICT (order_id) DO NOTHING names this index's column.
            shipment.HasIndex(s => s.OrderId).IsUnique();

            // "My shipments", newest first.
            shipment.HasIndex(s => new { s.BuyerId, s.CreatedAt });

            // The courier's claim query (Task 4): open shipments, oldest first. Finished ones are never deleted, so the index holds
            // only the few that are still open.
            shipment.HasIndex(s => s.CreatedAt)
                .HasFilter("courier_finished_at IS NULL")
                .HasDatabaseName("ix_shipments_courier_open");
        });

        modelBuilder.Entity<ShipmentPosition>(position =>
        {
            position.HasKey(p => new { p.ShipmentId, p.Seq });
            position.Property(p => p.Status).HasConversion<string>().HasMaxLength(16);
            position.HasOne<Shipment>().WithMany().HasForeignKey(p => p.ShipmentId).OnDelete(DeleteBehavior.Cascade);
        });

        // Delivery publishes no event, so the outbox stays empty; AddMessaging needs the table for its relay.
        modelBuilder.AddOutbox();
        modelBuilder.AddInbox();
    }
}
