using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyOnlineShop.Payment.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentReconciliationReason : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReconciliationReason",
                schema: "payment",
                table: "Payments",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new System.NotSupportedException("Payment reconciliation diagnostics cannot be deleted by migration downgrade.");
        }
    }
}
