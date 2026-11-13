using Microsoft.EntityFrameworkCore;

using NewStore.Messaging;

namespace NewStore.Inventory;

public class InventoryDbContext(DbContextOptions<InventoryDbContext> options) : DbContext(options)
{
    public DbSet<Edition> Editions => Set<Edition>();
    public DbSet<Reservation> Reservations => Set<Reservation>();
    public DbSet<Copy> Copies => Set<Copy>();
    public DbSet<EditionSnapshotRow> EditionSnapshots => Set<EditionSnapshotRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Edition>(edition =>
        {
            edition.Property(e => e.Id).ValueGeneratedNever();
            edition.Property(e => e.Format).HasConversion<string>().HasMaxLength(16);
            edition.Property(e => e.Price).HasColumnType("numeric(10,2)");

            // xmin is a system column: no migration column, but EF puts it in the WHERE of every UPDATE.
            edition.Property(e => e.Version).IsRowVersion();

            edition.ToTable(t => t.HasCheckConstraint("ck_editions_copies_total", "copies_total BETWEEN 1 AND 10000"));
        });

        modelBuilder.Entity<Reservation>(reservation =>
        {
            reservation.Property(r => r.Id).ValueGeneratedNever();
            reservation.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);
            reservation.HasOne<Edition>().WithMany().HasForeignKey(r => r.EditionId).OnDelete(DeleteBehavior.Restrict);

            // C-1: one live reservation per buyer and edition. The database enforces it, so 50 parallel
            // requests of one buyer cannot all pass an application-level check.
            reservation.HasIndex(r => new { r.EditionId, r.BuyerId })
                .IsUnique()
                .HasFilter("status IN ('Pending', 'Held', 'Confirmed')");

            // The sweeper's lookup.
            reservation.HasIndex(r => new { r.Status, r.ExpiresAt });
        });

        modelBuilder.Entity<Copy>(copy =>
        {
            copy.Property(c => c.Id).ValueGeneratedNever();
            copy.HasOne<Edition>().WithMany().HasForeignKey(c => c.EditionId).OnDelete(DeleteBehavior.Restrict);
            copy.HasOne<Reservation>().WithMany().HasForeignKey(c => c.ReservationId).OnDelete(DeleteBehavior.Restrict);

            // C-3: a copy number exists once per edition; and one reservation yields at most one copy.
            copy.HasIndex(c => new { c.EditionId, c.Number }).IsUnique();
            copy.HasIndex(c => c.ReservationId).IsUnique();
        });

        modelBuilder.Entity<EditionSnapshotRow>(snapshot =>
        {
            snapshot.HasKey(s => s.EditionId);
            snapshot.Property(s => s.EditionId).ValueGeneratedNever();
            snapshot.Property(s => s.Price).HasColumnType("numeric(10,2)");
            snapshot.HasOne<Edition>().WithMany().HasForeignKey(s => s.EditionId).OnDelete(DeleteBehavior.Restrict);
        });

        // Inventory publishes through the outbox. It consumes nothing yet, so there is no AddInbox.
        modelBuilder.AddOutbox();
    }
}
