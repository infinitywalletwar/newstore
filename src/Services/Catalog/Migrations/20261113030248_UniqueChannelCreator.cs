using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewStore.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class UniqueChannelCreator : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_channels_creator_id",
                table: "channels",
                column: "creator_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_channels_creator_id",
                table: "channels");
        }
    }
}
