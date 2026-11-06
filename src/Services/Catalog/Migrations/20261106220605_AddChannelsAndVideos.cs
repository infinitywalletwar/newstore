// The scaffolder emits constant arrays inline; a migration runs once, so CA1861 does not apply.
#pragma warning disable CA1861

using System;

using Microsoft.EntityFrameworkCore.Migrations;

using NpgsqlTypes;

#nullable disable

namespace NewStore.Catalog.Migrations;

/// <inheritdoc />
public partial class AddChannelsAndVideos : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "channels",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                creator_id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                slug = table.Column<string>(type: "text", nullable: false),
                description = table.Column<string>(type: "text", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_channels", x => x.id);
                table.CheckConstraint("ck_channels_slug_format", "slug ~ '^[a-z0-9-]+$'");
            });

        migrationBuilder.CreateTable(
            name: "videos",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                channel_id = table.Column<Guid>(type: "uuid", nullable: false),
                title = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                description = table.Column<string>(type: "text", nullable: false),
                duration_seconds = table.Column<int>(type: "integer", nullable: false),
                published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                search_vector = table.Column<NpgsqlTsVector>(type: "tsvector", nullable: false)
                    .Annotation("Npgsql:TsVectorConfig", "simple")
                    .Annotation("Npgsql:TsVectorProperties", new[] { "title", "description" })
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_videos", x => x.id);
                table.ForeignKey(
                    name: "fk_videos_channels_channel_id",
                    column: x => x.channel_id,
                    principalTable: "channels",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "ix_channels_slug",
            table: "channels",
            column: "slug",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_videos_channel_id_published_at",
            table: "videos",
            columns: new[] { "channel_id", "published_at" });

        migrationBuilder.CreateIndex(
            name: "ix_videos_published_at",
            table: "videos",
            column: "published_at");

        migrationBuilder.CreateIndex(
            name: "ix_videos_search_vector",
            table: "videos",
            column: "search_vector")
            .Annotation("Npgsql:IndexMethod", "GIN");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "videos");

        migrationBuilder.DropTable(
            name: "channels");
    }
}
