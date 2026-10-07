using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orders.Api.Migrations;

public partial class AddShipment : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Shipments",
            columns: table => new
            {
                Id = table.Column<Guid>(
                    type: "uuid",
                    nullable: false),

                OrderId = table.Column<Guid>(
                    type: "uuid",
                    nullable: false),

                TrackingNumber = table.Column<string>(
                    type: "character varying(100)",
                    maxLength: 100,
                    nullable: false),

                Carrier = table.Column<string>(
                    type: "character varying(50)",
                    maxLength: 50,
                    nullable: false),

                Status = table.Column<string>(
                    type: "character varying(20)",
                    maxLength: 20,
                    nullable: false),

                CreatedAtUtc = table.Column<DateTime>(
                    type: "timestamp with time zone",
                    nullable: false),

                ShippedAtUtc = table.Column<DateTime>(
                    type: "timestamp with time zone",
                    nullable: true),

                DeliveredAtUtc = table.Column<DateTime>(
                    type: "timestamp with time zone",
                    nullable: true),

                EstimatedDeliveryDateUtc = table.Column<DateTime>(
                    type: "timestamp with time zone",
                    nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey(
                    "PK_Shipments",
                    x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Shipments_OrderId",
            table: "Shipments",
            column: "OrderId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Shipments_TrackingNumber",
            table: "Shipments",
            column: "TrackingNumber",
            unique: true);
    }

    protected override void Down(
        MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "Shipments");
    }
}