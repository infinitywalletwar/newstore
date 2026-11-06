using Microsoft.EntityFrameworkCore;

using NewStore.Messaging.Outbox;

namespace NewStore.Messaging;

public static class MessagingModelBuilderExtensions
{
    /// <summary>
    /// Adds the <c>outbox_messages</c> table to the context's model. Table, column and index names are set
    /// explicitly, so the relay's raw SQL works whether or not the context uses a naming convention.
    /// </summary>
    public static ModelBuilder AddOutbox(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.ToTable("outbox_messages");
            entity.HasKey(m => m.Id);
            entity.Property(m => m.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(m => m.Topic).HasColumnName("topic");
            entity.Property(m => m.Key).HasColumnName("key");
            entity.Property(m => m.Type).HasColumnName("type");
            entity.Property(m => m.Payload).HasColumnName("payload").HasColumnType("jsonb");
            entity.Property(m => m.OccurredAt).HasColumnName("occurred_at");
            entity.Property(m => m.TraceParent).HasColumnName("trace_parent");
            entity.Property(m => m.PublishedAt).HasColumnName("published_at");
            entity.Property(m => m.Attempts).HasColumnName("attempts");
            entity.Property(m => m.LastError).HasColumnName("last_error");

            // The relay looks only at unsent rows; a partial index keeps it small however many sent rows pile up.
            entity.HasIndex(m => m.PublishedAt)
                .HasDatabaseName("ix_outbox_messages_unpublished")
                .HasFilter("published_at IS NULL");
        });

        return modelBuilder;
    }
}
