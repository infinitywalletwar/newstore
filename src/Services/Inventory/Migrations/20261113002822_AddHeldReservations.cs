using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewStore.Inventory.Migrations
{
    /// <inheritdoc />
    public partial class AddHeldReservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_reservations_edition_id_buyer_id",
                table: "reservations");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "held_at",
                table: "reservations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_reservations_edition_id_buyer_id",
                table: "reservations",
                columns: new[] { "edition_id", "buyer_id" },
                unique: true,
                filter: "status IN ('Pending', 'Held', 'Confirmed')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_reservations_edition_id_buyer_id",
                table: "reservations");

            migrationBuilder.DropColumn(
                name: "held_at",
                table: "reservations");

            migrationBuilder.CreateIndex(
                name: "ix_reservations_edition_id_buyer_id",
                table: "reservations",
                columns: new[] { "edition_id", "buyer_id" },
                unique: true,
                filter: "status IN ('Pending', 'Confirmed')");
        }
    }
}
