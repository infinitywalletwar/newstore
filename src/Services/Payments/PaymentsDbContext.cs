using Microsoft.EntityFrameworkCore;

using NewStore.Messaging;
using NewStore.Payments.Payments;

namespace NewStore.Payments;

public class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options) : DbContext(options)
{
    public DbSet<PaymentAttempt> PaymentAttempts => Set<PaymentAttempt>();

    public DbSet<ProviderEvent> ProviderEvents => Set<ProviderEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PaymentAttempt>(attempt =>
        {
            attempt.Property(a => a.Id).ValueGeneratedNever();
            attempt.Property(a => a.Amount).HasColumnType("numeric(10,2)");
            attempt.Property(a => a.Status).HasConversion<string>().HasMaxLength(16);

            // xmin is a system column: no migration column, but EF puts it in the WHERE of every UPDATE.
            attempt.Property(a => a.Version).IsRowVersion();

            // One payment per order. Two parallel creates meet here: the database lets one insert through.
            attempt.HasIndex(a => a.OrderId).IsUnique();

            // The webhook finds its payment by the provider's session id.
            attempt.HasIndex(a => a.ProviderSessionId).IsUnique();
        });

        modelBuilder.Entity<ProviderEvent>(providerEvent =>
        {
            providerEvent.HasKey(e => e.EventId);
            providerEvent.Property(e => e.EventId).ValueGeneratedNever();
        });

        // Payments publishes PaymentSucceeded, PaymentFailed and PaymentRefunded, and consumes RefundRequested.
        modelBuilder.AddOutbox();
        modelBuilder.AddInbox();
    }
}
