using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyOnlineShop.Pricing.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialPricing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "pricing");

            migrationBuilder.CreateTable(
                name: "Prices",
                schema: "pricing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductVariantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BasePrice = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    ComparePrice = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    Currency = table.Column<string>(type: "varchar(3)", unicode: false, maxLength: 3, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    EffectiveFromUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EffectiveToUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Prices", x => x.Id);
                    table.CheckConstraint("CK_Prices_Amounts", "[BasePrice] > 0 AND [BasePrice] <= 1000000000000 AND ([ComparePrice] IS NULL OR ([ComparePrice] >= [BasePrice] AND [ComparePrice] <= 1000000000000))");
                    table.CheckConstraint("CK_Prices_Currency", "[Currency] IN ('IRR','USD','EUR','GBP','AED','TRY')");
                    table.CheckConstraint("CK_Prices_Period", "[EffectiveToUtc] IS NULL OR [EffectiveToUtc] > [EffectiveFromUtc]");
                });

            migrationBuilder.CreateTable(
                name: "History",
                schema: "pricing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PriceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductVariantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BasePrice = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    ComparePrice = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    Currency = table.Column<string>(type: "varchar(3)", unicode: false, maxLength: 3, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    EffectiveFromUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EffectiveToUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Action = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_History", x => x.Id);
                    table.ForeignKey(
                        name: "FK_History_Prices_PriceId",
                        column: x => x.PriceId,
                        principalSchema: "pricing",
                        principalTable: "Prices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_History_PriceId",
                schema: "pricing",
                table: "History",
                column: "PriceId");

            migrationBuilder.CreateIndex(
                name: "IX_History_ProductVariantId_Currency_AtUtc",
                schema: "pricing",
                table: "History",
                columns: new[] { "ProductVariantId", "Currency", "AtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Prices_ProductVariantId_Currency_EffectiveFromUtc",
                schema: "pricing",
                table: "Prices",
                columns: new[] { "ProductVariantId", "Currency", "EffectiveFromUtc" },
                unique: true,
                filter: "[IsActive] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_Prices_ProductVariantId_Currency_IsActive_EffectiveFromUtc_EffectiveToUtc",
                schema: "pricing",
                table: "Prices",
                columns: new[] { "ProductVariantId", "Currency", "IsActive", "EffectiveFromUtc", "EffectiveToUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "History",
                schema: "pricing");

            migrationBuilder.DropTable(
                name: "Prices",
                schema: "pricing");
        }
    }
}
