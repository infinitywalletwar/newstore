using Microsoft.EntityFrameworkCore;

using NewStore.Catalog.Channels;
using NewStore.Catalog.Editions;
using NewStore.Catalog.Videos;
using NewStore.Messaging;

namespace NewStore.Catalog;

public class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    /// <summary>One channel per creator. The create endpoint tells this unique index's violation from the slug's by this name.</summary>
    public const string ChannelCreatorIndex = "ix_channels_creator_id";

    public DbSet<Channel> Channels => Set<Channel>();

    public DbSet<Video> Videos => Set<Video>();

    public DbSet<EditionCopy> Editions => Set<EditionCopy>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddOutbox();
        modelBuilder.AddInbox();

        modelBuilder.Entity<Channel>(channel =>
        {
            channel.Property(c => c.Name).HasMaxLength(Channel.NameMaxLength);
            channel.HasIndex(c => c.Slug).IsUnique();
            channel.HasIndex(c => c.CreatorId).IsUnique().HasDatabaseName(ChannelCreatorIndex);
            // Slugs are lower-case [a-z0-9-]; the database refuses anything else.
            channel.ToTable(t => t.HasCheckConstraint("ck_channels_slug_format", "slug ~ '^[a-z0-9-]+$'"));
        });

        modelBuilder.Entity<Video>(video =>
        {
            video.Property(v => v.Title).HasMaxLength(Video.TitleMaxLength);
            video.HasOne<Channel>().WithMany().HasForeignKey(v => v.ChannelId).OnDelete(DeleteBehavior.Cascade);
            // Channel pages and the newest-first browse both order by published_at.
            video.HasIndex(v => new { v.ChannelId, v.PublishedAt });
            video.HasIndex(v => v.PublishedAt);
            // A generated column keeps the tsvector in step with the text without app code; the GIN index serves @@ matches.
            video.HasGeneratedTsVectorColumn(v => v.SearchVector, "simple", v => new { v.Title, v.Description })
                .HasIndex(v => v.SearchVector)
                .HasMethod("GIN");
        });

        modelBuilder.Entity<EditionCopy>(edition =>
        {
            edition.ToTable("editions");
            edition.Property(e => e.Id).ValueGeneratedNever();
            edition.HasIndex(e => e.VideoId);
        });
    }
}
