using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyOnlineShop.Order.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderShipping : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Orders_Totals",
                schema: "ordering",
                table: "Orders");

            migrationBuilder.AddColumn<decimal>(
                name: "ShippingCost",
                schema: "ordering",
                table: "Orders",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "Shipping_Address_Building",
                schema: "ordering",
                table: "Orders",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Shipping_Address_City",
                schema: "ordering",
                table: "Orders",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Shipping_Address_CountryCode",
                schema: "ordering",
                table: "Orders",
                type: "varchar(2)",
                unicode: false,
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Shipping_Address_PhoneNumber",
                schema: "ordering",
                table: "Orders",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Shipping_Address_PostalCode",
                schema: "ordering",
                table: "Orders",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Shipping_Address_Recipient",
                schema: "ordering",
                table: "Orders",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Shipping_Address_State",
                schema: "ordering",
                table: "Orders",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Shipping_Address_Street",
                schema: "ordering",
                table: "Orders",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Shipping_Address_Unit",
                schema: "ordering",
                table: "Orders",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Shipping_Cost",
                schema: "ordering",
                table: "Orders",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Shipping_Currency",
                schema: "ordering",
                table: "Orders",
                type: "varchar(3)",
                unicode: false,
                maxLength: 3,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Shipping_MethodCode",
                schema: "ordering",
                table: "Orders",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Shipping_MethodName",
                schema: "ordering",
                table: "Orders",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "Shipping_QuotedAtUtc",
                schema: "ordering",
                table: "Orders",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Shipping_RequiresTracking",
                schema: "ordering",
                table: "Orders",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "Shipping_ShippingMethodId",
                schema: "ordering",
                table: "Orders",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Orders_Totals",
                schema: "ordering",
                table: "Orders",
                sql: "[Subtotal] > 0 AND [Subtotal] <= 1000000000000 AND [DiscountTotal] >= 0 AND [DiscountTotal] <= [Subtotal] AND [ShippingCost] >= 0 AND [ShippingCost] <= 1000000000000 AND [PayableAmount] = [Subtotal] - [DiscountTotal] + [ShippingCost] AND [PayableAmount] <= 1000000000000");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Shipping/order history cannot be deleted by migration downgrade.");
        }
    }
}
