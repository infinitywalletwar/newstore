using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewStore.Inventory.Migrations
{
    /// <inheritdoc />
    public partial class BackfillEditionLock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Editions reserved before AddEditionLockAndEnd are locked too: the earliest reservation, in any status.
            migrationBuilder.Sql(
                """
                UPDATE editions e SET locked_at = r.first_reserved_at
                FROM (SELECT edition_id, min(reserved_at) AS first_reserved_at FROM reservations GROUP BY edition_id) r
                WHERE r.edition_id = e.id AND e.locked_at IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to undo: rolling back AddEditionLockAndEnd drops the column.
        }
    }
}
