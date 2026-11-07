using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewStore.Inventory.Migrations
{
    /// <inheritdoc />
    public partial class AddEditionLockAndEnd : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ended_at",
                table: "editions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "locked_at",
                table: "editions",
                type: "timestamp with time zone",
                nullable: true);

            // Editions reserved before this migration are locked too: the earliest reservation, in any status.
            migrationBuilder.Sql(
                """
                UPDATE editions e SET locked_at = r.first_reserved_at
                FROM (SELECT edition_id, min(reserved_at) AS first_reserved_at FROM reservations GROUP BY edition_id) r
                WHERE r.edition_id = e.id;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ended_at",
                table: "editions");

            migrationBuilder.DropColumn(
                name: "locked_at",
                table: "editions");
        }
    }
}
