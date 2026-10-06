# Wallet — Phase 9

Wallet owns its Domain, Contracts, Application, Infrastructure and Presentation projects, `WalletDbContext`, `wallet` schema and migration history. It reuses Foundation configuration, response/error handling, correlation, logging and SQL Server infrastructure, Identity authentication/permissions and user-reference queries, and the existing Pricing.Contracts money rules. It reads no other module's tables or internal entities.

## API and authorization

* `GET /api/v1/wallet` reads the authenticated owner's primary wallet.
* `GET /api/v1/wallet/transactions` reads only that owner's ledger. Filters: `Type=Credit|Debit`, inclusive `FromUtc`, exclusive `ToUtc`, `Page` and `PageSize` (1–100).
* `POST /api/v1/admin/wallets/{userId}/credit` requires `wallet.credit`. Body: `IdempotencyKey` (nonempty GUID), positive decimal `Amount`, `Currency`, and required `Description` (maximum 500 characters). The existing Administrator role receives this permission through AddWalletPermission; authorized permission delegation uses existing Identity administration.

Owner and actor are taken from the authenticated `sub` claim. The target-user route is permitted only at the administrative boundary and must resolve through Identity. Client-supplied actor, owner, balance, direction or status fields do not determine financial values. Responses use DTOs and the existing ApiResponse envelope; validation/not-found/conflict errors use safe Problem Details with correlation. No public debit or customer credit endpoint exists.

Reads create nothing and return 404 until the first successful credit creates the wallet. One primary wallet per user is enforced by a unique index. Currency is selected by that first authorized credit and cannot subsequently change.

## Financial rules

All money is `decimal`, mapped consistently to SQL Server `decimal(18,4)`. Shared currency rules support IRR whole units and USD/EUR/GBP/AED/TRY with two fractional digits. Unsupported precision is rejected, never silently rounded. Inputs are positive and bounded by 1,000,000,000,000; balances remain between zero and that bound. Currency conversion is not implemented.

Ledger amounts are signed: Credit **+amount**, Debit **−amount**. Every posted entry satisfies `BalanceAfter = BalanceBefore + Amount`. Only committed successful postings have status Posted. Failed debits produce no successful ledger entry and consume no idempotency key. Ledger history is the source of financial history; the current balance is a materialized value updated with the ledger in one transaction.

Entries retain wallet, currency, signed amount, direction/status, before/after balances, required business reference, idempotency key, request fingerprint, description, actor, UTC timestamp and correlation. Fingerprints remain internal. Production Wallet code does not log request bodies, descriptions, authentication credentials, tokens or other sensitive payloads. Existing logging retains safe error messages and correlation.

## Atomicity, concurrency and retries

Each command executes inside a module-owned SQL transaction and the existing provider execution strategy. A transaction-owned exclusive `sp_getapplock` on the owner serializes first creation, postings and replays for the same primary wallet. A finite lock timeout becomes a retryable 409 with the same idempotency key.

The balance change uses a parameterized conditional SQL UPDATE with OUTPUT of the actual before/after values and a checked one-row result. Its WHERE clause enforces nonnegative balance and the upper bound; it does not depend on an unprotected read/check/write sequence. The signed ledger insert and balance UPDATE commit together. Any failure rolls back the operation, including a newly created wallet. Rowversion is mapped on the wallet root.

Idempotency is persisted on the ledger, unique per `(WalletId, IdempotencyKey)`. The fingerprint includes normalized currency/description, owner, actor, direction, canonical decimal amount and business reference. Identical replays return the original posting, including its historical balances, even after later postings; changed payload or actor returns 409. Replays resolve before current user/reference validation. A second unique index on `(WalletId, Type, ReferenceType, ReferenceId)` prevents posting the same business reference again under a new key. References obey the deployed SQL Server collation; callers should use stable canonical identifiers.

EF posting guards require exactly one matching new ledger entry for each recorded balance change, reject unpaired balance updates, wallet deletion and historical ledger edits/deletes. Per-context pending balance records validate transaction assembly; they are **not** idempotency storage. A SQL trigger additionally rejects all ledger UPDATE/DELETE commands, and restrictive foreign keys protect referenced wallets. InitialWallet deliberately refuses a destructive Down migration; financial schema evolution must use reviewed forward migrations. Corrections require new authorized postings, never rewritten history.

## Trusted integration contracts

`IWalletOperations.CreditAsync` and `DebitAsync` accept server-authorized owner/actor and a WalletOperation with amount, currency, key, description and business reference. They are trusted application boundaries: future workflows must establish authorization and provide stable retry/business identifiers before calling them. `IWalletAdministration` adapts administrative input into a server-defined AdministrativeCredit reference. `IWalletQueries` serves projected owner reads independently of commands.

Payment is absent from the repository and Phase 8 remains unimplemented. No payment-from-wallet, refund workflow, payment gateway, inventory change, checkout change, Redis, RabbitMQ or outbox was added. Wallet commits its own local transaction; cross-module financial workflows and their consistency protocol remain future work.

## Migrations and validation

InitialWallet and AddWalletPermission were generated and reviewed offline. Review scripts are `artifacts/wallet-migration.sql` and `artifacts/wallet-permission-migration.sql` (generated artifacts follow existing ignore conventions). No migration was applied and no local/persistent database was accessed or modified. No SQL Server, LocalDB or Docker was installed or launched.

Database-free tests use explicit EF InMemory command substitutes and fake authenticated claims/user references. They verify application rules, idempotency, owner isolation, authorization, API envelopes/errors, immutable EF history, SQL model/migration metadata and query translation with all connection attempts blocked. These substitutes do not prove SQL transaction or concurrency behavior.

Final validation: affected build succeeded with zero warnings/errors; Wallet 20 passed, Identity HTTP regressions 8 passed, Foundation 15 passed. Six Wallet SQL tests and one Foundation SQL connectivity test were skipped. Wallet and Identity model/migration consistency checks passed offline.

Six SQL integration tests are implemented but skipped while SQL Server is unavailable: simultaneous 700,000 debits from 1,000,000 with exactly one success and balance 300,000; concurrent first credit and duplicate credit/debit replay; rollback after balance update; rollback after both balance/ledger flushes; unique primary wallet/key/reference constraints; SQL immutable-history trigger and restrictive foreign key.

For a future explicitly authorized disposable SQL Server run, set `WALLET_TEST_SQL_SERVER` and run the Wallet test project. The fixture creates an isolated GUID-named `MyOnlineShop_WalletTests_...` database, applies only Wallet migrations, and validates its generated name before cleanup. It rejects LocalDB and never uses an existing application database. It does not start Docker. Clear the variable to keep these tests skipped during database-free validation.
