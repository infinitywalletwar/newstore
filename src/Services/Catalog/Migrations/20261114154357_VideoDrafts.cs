using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewStore.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class VideoDrafts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "published_at",
                table: "videos",
                type: "timestamp with time zone",
                nullable: true,
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone");

            // created_at and status are added nullable, filled, then made required, so they end with no default:
            // the application always writes them.
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "created_at",
                table: "videos",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "playlist_path",
                table: "videos",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "status",
                table: "videos",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "thumbnail_path",
                table: "videos",
                type: "text",
                nullable: true);

            // Every video that exists now was published before drafts existed, and plays the sample files
            // (Video.SamplePlaylistPath and Video.SampleThumbnailPath), so a stack seeded in Phase 1 needs no reset.
            migrationBuilder.Sql(
                """
                UPDATE videos
                SET status = 'Published',
                    created_at = published_at,
                    playlist_path = 'hls/sample/master.m3u8',
                    thumbnail_path = 'hls/sample/thumbnail.jpg'
                """);

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "created_at",
                table: "videos",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "status",
                table: "videos",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // published_at is required again, so a draft (which has none) cannot stay.
            migrationBuilder.Sql("DELETE FROM videos WHERE published_at IS NULL");

            migrationBuilder.DropColumn(
                name: "created_at",
                table: "videos");

            migrationBuilder.DropColumn(
                name: "playlist_path",
                table: "videos");

            migrationBuilder.DropColumn(
                name: "status",
                table: "videos");

            migrationBuilder.DropColumn(
                name: "thumbnail_path",
                table: "videos");

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "published_at",
                table: "videos",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)),
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone",
                oldNullable: true);
        }
    }
}
