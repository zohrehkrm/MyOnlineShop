using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyOnlineShop.Inventory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialInventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "inventory");

            migrationBuilder.CreateTable(
                name: "Warehouses",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Code = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Warehouses", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Stocks",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WarehouseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductVariantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Quantity = table.Column<long>(type: "bigint", nullable: false),
                    LowStockThreshold = table.Column<long>(type: "bigint", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Stocks", x => x.Id);
                    table.CheckConstraint("CK_Stocks_Quantity", "[Quantity] >= 0 AND [Quantity] <= 1000000000000");
                    table.CheckConstraint("CK_Stocks_Threshold", "[LowStockThreshold] >= 0 AND [LowStockThreshold] <= 1000000000000");
                    table.ForeignKey(
                        name: "FK_Stocks_Warehouses_WarehouseId",
                        column: x => x.WarehouseId,
                        principalSchema: "inventory",
                        principalTable: "Warehouses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Movements",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StockId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QuantityDelta = table.Column<long>(type: "bigint", nullable: false),
                    QuantityBefore = table.Column<long>(type: "bigint", nullable: false),
                    QuantityAfter = table.Column<long>(type: "bigint", nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestHash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Movements", x => x.Id);
                    table.CheckConstraint("CK_Movements_Quantity", "[QuantityDelta] <> 0 AND [QuantityBefore] >= 0 AND [QuantityAfter] >= 0 AND [QuantityAfter] <= 1000000000000 AND [QuantityAfter] = [QuantityBefore] + [QuantityDelta]");
                    table.CheckConstraint("CK_Movements_Type", "([Type] IN (1, 3) AND [QuantityDelta] > 0) OR ([Type] IN (2, 4) AND [QuantityDelta] < 0) OR [Type] = 5");
                    table.ForeignKey(
                        name: "FK_Movements_Stocks_StockId",
                        column: x => x.StockId,
                        principalSchema: "inventory",
                        principalTable: "Stocks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Adjustments",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MovementId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Adjustments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Adjustments_Movements_MovementId",
                        column: x => x.MovementId,
                        principalSchema: "inventory",
                        principalTable: "Movements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Receipts",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MovementId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Receipts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Receipts_Movements_MovementId",
                        column: x => x.MovementId,
                        principalSchema: "inventory",
                        principalTable: "Movements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Adjustments_MovementId",
                schema: "inventory",
                table: "Adjustments",
                column: "MovementId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Movements_OperationId",
                schema: "inventory",
                table: "Movements",
                column: "OperationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Movements_Reference",
                schema: "inventory",
                table: "Movements",
                column: "Reference");

            migrationBuilder.CreateIndex(
                name: "IX_Movements_StockId_CreatedAtUtc",
                schema: "inventory",
                table: "Movements",
                columns: new[] { "StockId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Movements_Type_CreatedAtUtc",
                schema: "inventory",
                table: "Movements",
                columns: new[] { "Type", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Receipts_MovementId",
                schema: "inventory",
                table: "Receipts",
                column: "MovementId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Stocks_ProductVariantId",
                schema: "inventory",
                table: "Stocks",
                column: "ProductVariantId");

            migrationBuilder.CreateIndex(
                name: "IX_Stocks_WarehouseId_ProductVariantId",
                schema: "inventory",
                table: "Stocks",
                columns: new[] { "WarehouseId", "ProductVariantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Warehouses_Code",
                schema: "inventory",
                table: "Warehouses",
                column: "Code",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Adjustments",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "Receipts",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "Movements",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "Stocks",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "Warehouses",
                schema: "inventory");
        }
    }
}
