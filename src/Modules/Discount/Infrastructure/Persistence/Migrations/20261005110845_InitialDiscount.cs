using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyOnlineShop.Discount.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialDiscount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "discount");

            migrationBuilder.CreateTable(
                name: "Rules",
                schema: "discount",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Type = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Value = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    Currency = table.Column<string>(type: "varchar(3)", unicode: false, maxLength: 3, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    StartsAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EndsAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    MinimumOrderAmount = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    UsageLimit = table.Column<int>(type: "int", nullable: true),
                    UsedCount = table.Column<int>(type: "int", nullable: false),
                    ProductVariantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CategoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CouponCode = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Rules", x => x.Id);
                    table.CheckConstraint("CK_Rules_Currency", "[Currency] IN ('IRR','USD','EUR','GBP','AED','TRY')");
                    table.CheckConstraint("CK_Rules_Limits", "[Priority] >= 0 AND [Priority] <= 10000 AND [MinimumOrderAmount] >= 0 AND [MinimumOrderAmount] <= 1000000000000 AND [UsedCount] >= 0 AND ([UsageLimit] IS NULL OR ([UsageLimit] > 0 AND [UsedCount] <= [UsageLimit]))");
                    table.CheckConstraint("CK_Rules_Period", "[EndsAtUtc] > [StartsAtUtc]");
                    table.CheckConstraint("CK_Rules_Target", "(CASE WHEN [ProductVariantId] IS NULL THEN 0 ELSE 1 END + CASE WHEN [ProductId] IS NULL THEN 0 ELSE 1 END + CASE WHEN [CategoryId] IS NULL THEN 0 ELSE 1 END) <= 1");
                    table.CheckConstraint("CK_Rules_Value", "([Type] = 'Percentage' AND [Value] > 0 AND [Value] <= 100) OR ([Type] = 'Fixed' AND [Value] > 0 AND [Value] <= 1000000000000)");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Rules_CategoryId",
                schema: "discount",
                table: "Rules",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_Rules_Currency_CouponCode",
                schema: "discount",
                table: "Rules",
                columns: new[] { "Currency", "CouponCode" });

            migrationBuilder.CreateIndex(
                name: "IX_Rules_Currency_IsActive_StartsAtUtc_EndsAtUtc",
                schema: "discount",
                table: "Rules",
                columns: new[] { "Currency", "IsActive", "StartsAtUtc", "EndsAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Rules_ProductId",
                schema: "discount",
                table: "Rules",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_Rules_ProductVariantId",
                schema: "discount",
                table: "Rules",
                column: "ProductVariantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Rules",
                schema: "discount");
        }
    }
}
