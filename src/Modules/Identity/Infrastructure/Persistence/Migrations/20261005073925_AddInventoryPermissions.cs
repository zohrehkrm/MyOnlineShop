using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace MyOnlineShop.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInventoryPermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                schema: "identity",
                table: "Permissions",
                column: "Key",
                values: new object[]
                {
                    "inventory.adjust",
                    "inventory.deduct",
                    "inventory.receive",
                    "inventory.view"
                });

            migrationBuilder.InsertData(
                schema: "identity",
                table: "RolePermissions",
                columns: new[] { "PermissionKey", "RoleId" },
                values: new object[,]
                {
                    { "inventory.adjust", new Guid("00000000-0000-0000-0000-000000000001") },
                    { "inventory.deduct", new Guid("00000000-0000-0000-0000-000000000001") },
                    { "inventory.receive", new Guid("00000000-0000-0000-0000-000000000001") },
                    { "inventory.view", new Guid("00000000-0000-0000-0000-000000000001") }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                schema: "identity",
                table: "RolePermissions",
                keyColumns: new[] { "PermissionKey", "RoleId" },
                keyValues: new object[] { "inventory.adjust", new Guid("00000000-0000-0000-0000-000000000001") });

            migrationBuilder.DeleteData(
                schema: "identity",
                table: "RolePermissions",
                keyColumns: new[] { "PermissionKey", "RoleId" },
                keyValues: new object[] { "inventory.deduct", new Guid("00000000-0000-0000-0000-000000000001") });

            migrationBuilder.DeleteData(
                schema: "identity",
                table: "RolePermissions",
                keyColumns: new[] { "PermissionKey", "RoleId" },
                keyValues: new object[] { "inventory.receive", new Guid("00000000-0000-0000-0000-000000000001") });

            migrationBuilder.DeleteData(
                schema: "identity",
                table: "RolePermissions",
                keyColumns: new[] { "PermissionKey", "RoleId" },
                keyValues: new object[] { "inventory.view", new Guid("00000000-0000-0000-0000-000000000001") });

            migrationBuilder.DeleteData(
                schema: "identity",
                table: "Permissions",
                keyColumn: "Key",
                keyValue: "inventory.adjust");

            migrationBuilder.DeleteData(
                schema: "identity",
                table: "Permissions",
                keyColumn: "Key",
                keyValue: "inventory.deduct");

            migrationBuilder.DeleteData(
                schema: "identity",
                table: "Permissions",
                keyColumn: "Key",
                keyValue: "inventory.receive");

            migrationBuilder.DeleteData(
                schema: "identity",
                table: "Permissions",
                keyColumn: "Key",
                keyValue: "inventory.view");
        }
    }
}
