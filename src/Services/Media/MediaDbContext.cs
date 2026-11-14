using Microsoft.EntityFrameworkCore;

using NewStore.Messaging;

namespace NewStore.Media;

public class MediaDbContext(DbContextOptions<MediaDbContext> options) : DbContext(options)
{
    public DbSet<Upload> Uploads => Set<Upload>();
    public DbSet<ProcessingJob> ProcessingJobs => Set<ProcessingJob>();
    public DbSet<DigitalCopy> DigitalCopies => Set<DigitalCopy>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Upload>(upload =>
        {
            upload.Property(u => u.Id).ValueGeneratedNever();
            upload.Property(u => u.FileName).HasMaxLength(255);
            upload.Property(u => u.ContentType).HasMaxLength(100);
            upload.Property(u => u.ObjectKey).HasMaxLength(200);
            upload.Property(u => u.S3UploadId).HasMaxLength(512);
            upload.Property(u => u.Status).HasConversion<string>().HasMaxLength(16);
            upload.Property(u => u.FailureReason).HasMaxLength(200);

            // xmin is a system column: no migration column, but EF puts it in the WHERE of every UPDATE.
            upload.Property(u => u.Version).IsRowVersion();

            // One live upload per video. The database enforces it, so two parallel creates cannot both pass an application check.
            upload.HasIndex(u => u.VideoId)
                .IsUnique()
                .HasFilter("status IN ('Uploading', 'Processing', 'Published')")
                .HasDatabaseName("ix_uploads_live_video");

            upload.HasIndex(u => new { u.CreatorId, u.CreatedAt });

            // The expiry sweep's lookup.
            upload.HasIndex(u => new { u.Status, u.CreatedAt });
        });

        modelBuilder.Entity<ProcessingJob>(job =>
        {
            job.Property(j => j.Id).ValueGeneratedNever();
            job.Property(j => j.Status).HasConversion<string>().HasMaxLength(16);
            job.Property(j => j.LockedBy).HasMaxLength(100);
            job.HasOne<Upload>().WithOne().HasForeignKey<ProcessingJob>(j => j.UploadId).OnDelete(DeleteBehavior.Restrict);
            job.HasIndex(j => j.UploadId).IsUnique();

            // The worker's claim query.
            job.HasIndex(j => new { j.Status, j.NextRunAt });
        });

        modelBuilder.Entity<DigitalCopy>(copy =>
        {
            copy.HasKey(c => new { c.OwnerId, c.VideoId });
        });

        modelBuilder.AddOutbox();
        modelBuilder.AddInbox();
    }
}
