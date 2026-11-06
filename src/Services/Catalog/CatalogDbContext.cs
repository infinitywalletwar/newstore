using Microsoft.EntityFrameworkCore;

using NewStore.Catalog.Channels;
using NewStore.Catalog.Videos;

namespace NewStore.Catalog;

public class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    public DbSet<Channel> Channels => Set<Channel>();

    public DbSet<Video> Videos => Set<Video>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Channel>(channel =>
        {
            channel.Property(c => c.Name).HasMaxLength(Channel.NameMaxLength);
            channel.HasIndex(c => c.Slug).IsUnique();
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
    }
}
