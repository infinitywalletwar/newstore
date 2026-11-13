using Microsoft.EntityFrameworkCore;

using NewStore.Messaging;
using NewStore.Orders.Orders;

namespace NewStore.Orders;

public class OrdersDbContext(DbContextOptions<OrdersDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>(order =>
        {
            order.Property(o => o.Id).ValueGeneratedNever();
            order.Property(o => o.Format).HasMaxLength(32);
            order.Property(o => o.Price).HasColumnType("numeric(10,2)");
            order.Property(o => o.Status).HasConversion<string>().HasMaxLength(16);
            order.Property(o => o.CheckoutUrl).HasMaxLength(2048);

            // xmin is a PostgreSQL system column: the migration lists it only so EF knows the property is mapped, and it creates nothing. EF puts it in the WHERE of every UPDATE.
            // A pay request and a payment event that change the same order meet here: the later save fails and reloads.
            order.Property(o => o.Version).IsRowVersion();

            // The open-order lookup of "buy".
            order.HasIndex(o => new { o.BuyerId, o.EditionId });

            // "My copies".
            order.HasIndex(o => new { o.BuyerId, o.Status });
        });

        // Orders publishes RefundRequested, and consumes PaymentSucceeded, PaymentFailed and PaymentRefunded.
        modelBuilder.AddOutbox();
        modelBuilder.AddInbox();
    }
}
