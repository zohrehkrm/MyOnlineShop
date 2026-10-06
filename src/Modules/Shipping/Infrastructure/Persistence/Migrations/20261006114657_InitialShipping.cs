using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyOnlineShop.Shipping.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialShipping : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "shipping");

            migrationBuilder.CreateTable(
                name: "Audit",
                schema: "shipping",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Action = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    AtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Audit", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Methods",
                schema: "shipping",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Code = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    BaseCost = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    Currency = table.Column<string>(type: "varchar(3)", unicode: false, maxLength: 3, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    RequiresTracking = table.Column<bool>(type: "bit", nullable: false),
                    Revision = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Methods", x => x.Id);
                    table.CheckConstraint("CK_ShippingMethods_Cost", "[BaseCost] >= 0 AND [BaseCost] <= 1000000000000");
                    table.CheckConstraint("CK_ShippingMethods_Currency", "[Currency] IN ('IRR','USD','EUR','GBP','AED','TRY')");
                });

            migrationBuilder.CreateTable(
                name: "Shipments",
                schema: "shipping",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ShippingMethodId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MethodName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    MethodCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Address_Recipient = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Address_PhoneNumber = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Address_State = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Address_City = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Address_Street = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Address_PostalCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Address_CountryCode = table.Column<string>(type: "varchar(2)", unicode: false, maxLength: 2, nullable: false),
                    Address_Building = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Address_Unit = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    ShippingCost = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    Currency = table.Column<string>(type: "varchar(3)", unicode: false, maxLength: 3, nullable: false),
                    RequiresTracking = table.Column<bool>(type: "bit", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    TrackingNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Carrier = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ShippedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeliveredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Revision = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Shipments", x => x.Id);
                    table.CheckConstraint("CK_Shipments_Cost", "[ShippingCost] >= 0 AND [ShippingCost] <= 1000000000000");
                    table.CheckConstraint("CK_Shipments_Currency", "[Currency] IN ('IRR','USD','EUR','GBP','AED','TRY')");
                    table.CheckConstraint("CK_Shipments_Status", "[Status] IN ('Pending','Preparing','Shipped','InTransit','Delivered','Cancelled')");
                    table.CheckConstraint("CK_Shipments_Timestamps", "([Status] IN ('Pending','Preparing','Cancelled') AND [ShippedAtUtc] IS NULL AND [DeliveredAtUtc] IS NULL) OR ([Status] IN ('Shipped','InTransit') AND [ShippedAtUtc] IS NOT NULL AND [ShippedAtUtc] >= [CreatedAtUtc] AND [DeliveredAtUtc] IS NULL) OR ([Status] = 'Delivered' AND [ShippedAtUtc] IS NOT NULL AND [DeliveredAtUtc] IS NOT NULL AND [ShippedAtUtc] >= [CreatedAtUtc] AND [DeliveredAtUtc] >= [ShippedAtUtc])");
                    table.CheckConstraint("CK_Shipments_Tracking", "[Status] NOT IN ('Shipped','InTransit','Delivered') OR [RequiresTracking] = 0 OR ([TrackingNumber] IS NOT NULL AND [Carrier] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_Shipments_Methods_ShippingMethodId",
                        column: x => x.ShippingMethodId,
                        principalSchema: "shipping",
                        principalTable: "Methods",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Audit_EntityId_AtUtc",
                schema: "shipping",
                table: "Audit",
                columns: new[] { "EntityId", "AtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Methods_Code",
                schema: "shipping",
                table: "Methods",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Shipments_OrderId",
                schema: "shipping",
                table: "Shipments",
                column: "OrderId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Shipments_ShippingMethodId",
                schema: "shipping",
                table: "Shipments",
                column: "ShippingMethodId");

            migrationBuilder.CreateIndex(
                name: "IX_Shipments_Status_CreatedAtUtc",
                schema: "shipping",
                table: "Shipments",
                columns: new[] { "Status", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Shipments_UserId_CreatedAtUtc",
                schema: "shipping",
                table: "Shipments",
                columns: new[] { "UserId", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Shipping/order history cannot be deleted by migration downgrade.");
        }
    }
}
