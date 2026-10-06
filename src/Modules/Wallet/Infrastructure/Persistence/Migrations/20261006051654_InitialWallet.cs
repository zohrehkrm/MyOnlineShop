using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyOnlineShop.Wallet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialWallet : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "wallet");

            migrationBuilder.CreateTable(
                name: "Wallets",
                schema: "wallet",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Currency = table.Column<string>(type: "varchar(3)", unicode: false, maxLength: 3, nullable: false),
                    Balance = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Wallets", x => x.Id);
                    table.CheckConstraint("CK_Wallets_Balance", "[Balance] >= 0 AND [Balance] <= 1000000000000");
                    table.CheckConstraint("CK_Wallets_Currency", "[Currency] IN ('IRR','USD','EUR','GBP','AED','TRY')");
                });

            migrationBuilder.CreateTable(
                name: "Ledger",
                schema: "wallet",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WalletId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    Currency = table.Column<string>(type: "varchar(3)", unicode: false, maxLength: 3, nullable: false),
                    Type = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    BalanceBefore = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    BalanceAfter = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    ReferenceType = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    ReferenceId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    IdempotencyKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestFingerprint = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Ledger", x => x.Id);
                    table.CheckConstraint("CK_WalletLedger_Amount", "(([Type] = 'Credit' AND [Amount] > 0) OR ([Type] = 'Debit' AND [Amount] < 0)) AND ABS([Amount]) <= 1000000000000");
                    table.CheckConstraint("CK_WalletLedger_Balances", "[BalanceBefore] >= 0 AND [BalanceBefore] <= 1000000000000 AND [BalanceAfter] >= 0 AND [BalanceAfter] <= 1000000000000 AND [BalanceAfter] = [BalanceBefore] + [Amount]");
                    table.CheckConstraint("CK_WalletLedger_Currency", "[Currency] IN ('IRR','USD','EUR','GBP','AED','TRY')");
                    table.CheckConstraint("CK_WalletLedger_Status", "[Status] = 'Posted'");
                    table.ForeignKey(
                        name: "FK_Ledger_Wallets_WalletId",
                        column: x => x.WalletId,
                        principalSchema: "wallet",
                        principalTable: "Wallets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Ledger_WalletId_CreatedAtUtc",
                schema: "wallet",
                table: "Ledger",
                columns: new[] { "WalletId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Ledger_WalletId_IdempotencyKey",
                schema: "wallet",
                table: "Ledger",
                columns: new[] { "WalletId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Ledger_WalletId_Type_ReferenceType_ReferenceId",
                schema: "wallet",
                table: "Ledger",
                columns: new[] { "WalletId", "Type", "ReferenceType", "ReferenceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Wallets_UserId",
                schema: "wallet",
                table: "Wallets",
                column: "UserId",
                unique: true);

            // EXEC keeps CREATE TRIGGER in its own batch, including idempotent migration scripts.
            migrationBuilder.Sql("""
                EXEC(N'CREATE TRIGGER [wallet].[TR_WalletLedger_Immutable]
                ON [wallet].[Ledger] AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    THROW 51019, ''Wallet ledger history is immutable.'', 1;
                END;');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Wallet migration rollback cannot delete financial history. Use a reviewed forward migration.");
        }
    }
}
