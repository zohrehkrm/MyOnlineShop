using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyOnlineShop.Refund.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialRefund : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "refund");

            migrationBuilder.CreateTable(
                name: "Refunds",
                schema: "refund",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PaymentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    Currency = table.Column<string>(type: "varchar(3)", unicode: false, maxLength: 3, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    IdempotencyKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DueAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ProcessedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    WalletTransactionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    FailureCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Refunds", x => x.Id);
                    table.CheckConstraint("CK_Refund_Amount", "[Amount] > 0 AND [Amount] <= 1000000000000");
                    table.CheckConstraint("CK_Refund_Completion", "([Status] = 'Completed' AND [ProcessedAtUtc] IS NOT NULL AND [WalletTransactionId] IS NOT NULL) OR ([Status] <> 'Completed' AND [ProcessedAtUtc] IS NULL AND [WalletTransactionId] IS NULL)");
                    table.CheckConstraint("CK_Refund_Currency", "[Currency] IN ('IRR','USD','EUR','GBP','AED','TRY')");
                    table.CheckConstraint("CK_Refund_Schedule", "[DueAtUtc] >= [CreatedAtUtc] AND [NextAttemptAtUtc] >= [DueAtUtc]");
                    table.CheckConstraint("CK_Refund_State", "[Status] IN ('Pending','Processing','Completed','Failed') AND [AttemptCount] >= 0");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Refunds_IdempotencyKey",
                schema: "refund",
                table: "Refunds",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Refunds_OrderId",
                schema: "refund",
                table: "Refunds",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_Refunds_PaymentId",
                schema: "refund",
                table: "Refunds",
                column: "PaymentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Refunds_Status_NextAttemptAtUtc",
                schema: "refund",
                table: "Refunds",
                columns: new[] { "Status", "NextAttemptAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Refund financial and idempotency history cannot be removed by migration downgrade.");
        }
    }
}
