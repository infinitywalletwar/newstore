using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewStore.Orders.Migrations
{
    /// <inheritdoc />
    public partial class DeliveryAddress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "delivery_address",
                table: "orders",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "delivery_latitude",
                table: "orders",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "delivery_longitude",
                table: "orders",
                type: "double precision",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "delivery_address",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "delivery_latitude",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "delivery_longitude",
                table: "orders");
        }
    }
}
