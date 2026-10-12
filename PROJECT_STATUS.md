# Project status

Updated 2026-10-12 (Asia/Tehran). This is the current status file; [previous phase history](PROJECT_STATUS.md.txt) is retained separately. Repository instructions and architecture were read from the existing AGENTS.md.txt and ARCHITECTURE.md.txt files.

## Current work: Code First initialization and default administrator

Implementation and database-free verification are complete. No SQL Server connection, migration application, account creation in a real database, infrastructure startup, or database-file/data modification took place during this implementation step. Live database initialization is implemented but unverified. The overall regression suite is not fully passing because of two existing Catalog/Persian-culture validation failures.

## Behavior and configuration

- The API composition root awaits ordered asynchronous EF migrations before starting the HTTP host or hosted workers. A mandatory initialization failure prevents the health endpoint/server from starting; an offline host test verifies this.
- Each existing context keeps its SQL Server provider, migration assembly, owner schema and history table. MigrateAsync creates a missing target when permitted, applies pending migrations, and uses the provider migration lock. There is no EnsureCreated, reset/delete path or distributed transaction.
- Each context has a separate scope. Failures report the context/stage and exception type, preserve the underlying cause for controlled internal diagnostics, and do not print raw exception messages/connection strings/passwords. Later steps and bootstrap stop. Cancellation propagates.
- Development JSON enables DatabaseInitialization:MigrateOnStartup by default. Base/Production JSON disables it; Production requires an explicit override. IdentityBootstrap:Enabled defaults to false in all environments.
- Bootstrap password validation runs before migration work when enabled. IdentityBootstrap:Password must come from secure configuration and pass the configured Identity validators plus the existing 8–128 character complexity/no-whitespace policy. No password was generated/configured for the actual default account.
- Supported UserManager<User>.CreateAsync runs through a custom password store over the existing Identity entities/repository/hasher. Existing email/JWT APIs and permission policies remain intact.
- Named account: username zohrehkrm, email zohrehkarimi1372@gmail.com, requested phone 09370747630 stored as +989370747630 under the existing E.164 convention.
- The existing seeded Administrator role and core identity.users.manage/identity.roles.manage grants must exist. No roles/permission grants are invented. Creation/link/audit are transactional and serialized by the existing SQL application lock.
- Repeat runs preserve the existing password and security stamp. An exactly matching active account can recover a missing Administrator link once, with audit/stamp invalidation. Existing email-only users or username/email/profile conflicts are rejected without adoption or password overwrite.
- The legacy BootstrapIdentityAdmin=true host mode is retired with a safe configuration error. The established first-administrator application command is retained for existing internal callers/tests.
- AddUserNames is the one required additive migration: two nullable Identity username columns and a filtered unique index. It does not seed an account/password, overwrite legacy usernames or replace old migrations.
- Redis/RabbitMQ are not dependencies of this initializer. Test hosts and the regression script explicitly disable migration/bootstrap and infrastructure connections.

Required keys: ConnectionStrings:SqlServer; DatabaseInitialization:MigrateOnStartup; IdentityBootstrap:Enabled; IdentityBootstrap:Password only when enabled; existing IdentitySecurity:SigningKeyBase64. Existing Database:CommandTimeoutSeconds/MaxRetryCount retain their defaults. Environment-variable equivalents use double underscores.

The effective inspected native Development SQL target is **localhost / MyOnlineShop**, from appsettings.Development.json, using Windows Authentication. No environment or Development User Secrets connection override was found. No configured bootstrap password was found. No credentials were printed. Database existence and account permissions were not inspected through a connection.

## DbContext order and ownership

All owner history tables are named __EFMigrationsHistory. The database-free ownership test verifies every provider, assembly, history schema, existing migration list and current model.

| Order | Context | History schema |
| --- | --- | --- |
| 1 | FoundationDbContext | foundation |
| 2 | IdentityDbContext | identity |
| 3 | CatalogDbContext | catalog |
| 4 | InventoryDbContext | inventory |
| 5 | CartDbContext | cart |
| 6 | PricingDbContext | pricing |
| 7 | DiscountDbContext | discount |
| 8 | MessagingDbContext | messaging |
| 9 | OrderDbContext | ordering |
| 10 | WalletDbContext | wallet |
| 11 | PaymentDbContext | payment |
| 12 | RefundDbContext | refund |
| 13 | ShippingDbContext | shipping |

Reporting has no DbContext. See [initialization documentation](DATABASE_INITIALIZATION.md) for owner project paths, SQL permissions, secure configuration and recovery.

## Verification results

- Initial affected Identity infrastructure build: succeeded, zero warnings/errors.
- Final affected API build: succeeded, zero warnings/errors, with separate output to avoid a running application's locked files.
- The additive migration was generated/reviewed offline; all 13 owner models have no pending model changes.
- Latest Foundation: 34 passed, 0 failed, 1 SQL skip. Identity: 48 passed, 0 failed, 13 SQL skips.
- Final unique full-suite aggregate: **390 passed, 2 failed, 71 skipped** across 14 suites. SQL/Redis/RabbitMQ integration variables were cleared in the regression child process. The aggregate command correctly returns failure rather than claiming success.
- No live SQL creation, migration replay, SQL bootstrap concurrency/rollback or permission verification was run.
- TRX files and full summary.json: artifacts/database-initialization-tests/ (ignored artifacts).
- Diff whitespace check passed.

| Suite | Passed | Failed | Skipped |
| --- | --- | --- | --- |
| Foundation | 34 | 0 | 1 |
| Identity | 48 | 0 | 13 |
| Catalog | 30 | 2 | 0 |
| Inventory | 17 | 0 | 8 |
| Cart | 13 | 0 | 5 |
| PricingDiscount | 36 | 0 | 5 |
| Order | 28 | 0 | 5 |
| Payment | 50 | 0 | 4 |
| Wallet | 22 | 0 | 6 |
| Messaging | 20 | 0 | 9 |
| Refund | 24 | 0 | 6 |
| Shipping | 20 | 0 | 6 |
| Caching | 19 | 0 | 1 |
| Reporting | 29 | 0 | 2 |
| Total | 390 | 2 | 71 |

New focused tests verify repeat initialization/order, every context's existing migration ownership/current model, failure reporting/short-circuiting, actual HTTP-host refusal on mandatory migration failure, safe missing configuration, cancellation, random password policy failures, username/email conflicts, duplicate prevention, supported Identity hashing, existing password/stamp preservation, missing seeded permissions, simulated parallel bootstrap, disabled bootstrap and inactive-account refusal. In-memory transaction/concurrency tests do not establish SQL Server lock/rollback behavior.

## Remaining blockers

1. Default administrator bootstrap remains disabled and has no securely configured password. Configure it intentionally before provisioning; disable bootstrap/remove its secret afterward if no startup provisioning is needed.
2. SQL login/create-database/schema/history/seed-write/application-lock permissions and live target state remain unverified. Initializer execution/migration application is a separate authorized step. No production readiness is claimed.
3. Catalog tests Management_requires_permission_and_reuses_response_and_error_contracts and Product_endpoints_create_variant_publish_update_and_paginate_search fail with HTTP 500. Existing decimal RangeAttribute limits use culture-sensitive parsing. A standalone .NET 10 diagnostic with no application/Identity/database code completes under en-US and throws ArgumentException under fa-IR. This pre-existing localization path was deliberately left unchanged to respect this task's scope.
4. Live SQL, RabbitMQ, Redis and real gateway/E2E validation from prior phases remain deferred.

## Changed files for this initialization task

Prior Swagger/localization/local SQL/JWT work was already present and was preserved. The list below records only files changed or added for this task; some also contain earlier work.

- [MyOnlineShop/Foundation/DatabaseInitializer.cs](MyOnlineShop/Foundation/DatabaseInitializer.cs)
- [MyOnlineShop/Program.cs](MyOnlineShop/Program.cs)
- [MyOnlineShop/appsettings.json](MyOnlineShop/appsettings.json)
- [MyOnlineShop/appsettings.Development.json](MyOnlineShop/appsettings.Development.json)
- [src/Modules/Identity/Contracts/IDefaultAdministratorBootstrap.cs](src/Modules/Identity/Contracts/IDefaultAdministratorBootstrap.cs)
- [src/Modules/Identity/Application/Abstractions.cs](src/Modules/Identity/Application/Abstractions.cs)
- [src/Modules/Identity/Domain/User.cs](src/Modules/Identity/Domain/User.cs)
- [src/Modules/Identity/Infrastructure/DependencyInjection.cs](src/Modules/Identity/Infrastructure/DependencyInjection.cs)
- [src/Modules/Identity/Infrastructure/Persistence/IdentityConfigurations.cs](src/Modules/Identity/Infrastructure/Persistence/IdentityConfigurations.cs)
- [src/Modules/Identity/Infrastructure/Persistence/IdentityStore.cs](src/Modules/Identity/Infrastructure/Persistence/IdentityStore.cs)
- [src/Modules/Identity/Infrastructure/Persistence/IdentityDesignTimeDbContextFactory.cs](src/Modules/Identity/Infrastructure/Persistence/IdentityDesignTimeDbContextFactory.cs)
- [src/Modules/Identity/Infrastructure/Security/BootstrapUserStore.cs](src/Modules/Identity/Infrastructure/Security/BootstrapUserStore.cs)
- [src/Modules/Identity/Infrastructure/Security/BootstrapPasswordValidator.cs](src/Modules/Identity/Infrastructure/Security/BootstrapPasswordValidator.cs)
- [src/Modules/Identity/Infrastructure/Security/DefaultAdministratorBootstrap.cs](src/Modules/Identity/Infrastructure/Security/DefaultAdministratorBootstrap.cs)
- [src/Modules/Identity/Infrastructure/Persistence/Migrations/20261011122924_AddUserNames.cs](src/Modules/Identity/Infrastructure/Persistence/Migrations/20261011122924_AddUserNames.cs)
- [src/Modules/Identity/Infrastructure/Persistence/Migrations/20261011122924_AddUserNames.Designer.cs](src/Modules/Identity/Infrastructure/Persistence/Migrations/20261011122924_AddUserNames.Designer.cs)
- [src/Modules/Identity/Infrastructure/Persistence/Migrations/IdentityDbContextModelSnapshot.cs](src/Modules/Identity/Infrastructure/Persistence/Migrations/IdentityDbContextModelSnapshot.cs)
- [tests/MyOnlineShop.Foundation.Tests/DatabaseInitializationTests.cs](tests/MyOnlineShop.Foundation.Tests/DatabaseInitializationTests.cs)
- [tests/MyOnlineShop.Identity.Tests/DefaultAdministratorBootstrapTests.cs](tests/MyOnlineShop.Identity.Tests/DefaultAdministratorBootstrapTests.cs)
- [tests/MyOnlineShop.Identity.Tests/InMemoryIdentityStore.cs](tests/MyOnlineShop.Identity.Tests/InMemoryIdentityStore.cs)
- [tests/MyOnlineShop.Foundation.Tests/FoundationTests.cs](tests/MyOnlineShop.Foundation.Tests/FoundationTests.cs)
- [tests/MyOnlineShop.Identity.Tests/IdentityFactory.cs](tests/MyOnlineShop.Identity.Tests/IdentityFactory.cs)
- [tests/MyOnlineShop.Identity.Tests/IdentityHttpTests.cs](tests/MyOnlineShop.Identity.Tests/IdentityHttpTests.cs)
- [tests/MyOnlineShop.Catalog.Tests/CatalogTestInfrastructure.cs](tests/MyOnlineShop.Catalog.Tests/CatalogTestInfrastructure.cs)
- [tests/MyOnlineShop.Inventory.Tests/InventoryApiAndModelTests.cs](tests/MyOnlineShop.Inventory.Tests/InventoryApiAndModelTests.cs)
- [tests/MyOnlineShop.Cart.Tests/CartTestInfrastructure.cs](tests/MyOnlineShop.Cart.Tests/CartTestInfrastructure.cs)
- [tests/MyOnlineShop.PricingDiscount.Tests/ApiTestSupport.cs](tests/MyOnlineShop.PricingDiscount.Tests/ApiTestSupport.cs)
- [tests/MyOnlineShop.Order.Tests/ApiTests.cs](tests/MyOnlineShop.Order.Tests/ApiTests.cs)
- [tests/MyOnlineShop.Wallet.Tests/ApiTests.cs](tests/MyOnlineShop.Wallet.Tests/ApiTests.cs)
- [tests/MyOnlineShop.Shipping.Tests/ApiTests.cs](tests/MyOnlineShop.Shipping.Tests/ApiTests.cs)
- [tests/MyOnlineShop.Reporting.Tests/ApiTests.cs](tests/MyOnlineShop.Reporting.Tests/ApiTests.cs)
- [scripts/Test-Regression.ps1](scripts/Test-Regression.ps1)
- [DATABASE_INITIALIZATION.md](DATABASE_INITIALIZATION.md)
- [README.md](README.md)
- [IDENTITY.md](IDENTITY.md)
- [DOCKER.md](DOCKER.md)
- [FOUNDATION.md](FOUNDATION.md)
- [PROJECT_STATUS.md](PROJECT_STATUS.md)
- [PROJECT_STATUS.md.txt](PROJECT_STATUS.md.txt)

The regression script's new startup guards protect database-free execution. Optional separate build output, no-restore, StartAt and ContinueOnFailure support reviewable regression evidence; failures remain failures.
