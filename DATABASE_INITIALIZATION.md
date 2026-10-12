# Database initialization and default administrator

Startup runs database initialization before `app.Run()` starts the web server or hosted workers. The existing SQL Server connection, module contexts, migration assemblies and schema-owned histories are reused. There is no `EnsureCreated`, database reset, cross-module transaction, or infrastructure-service startup in this initializer.

## Configuration

| Key | Default / purpose |
| --- | --- |
| `ConnectionStrings:SqlServer` | Existing shared SQL Server connection; inspected local Development target is `localhost`, database `MyOnlineShop`, Windows Authentication. |
| `DatabaseInitialization:MigrateOnStartup` | `true` in the committed Development settings; `false` in base settings and therefore Production unless explicitly overridden. |
| `IdentityBootstrap:Enabled` | `false` in base settings; explicitly opt in to creation of the named administrator. |
| `IdentityBootstrap:Password` | Required only when bootstrap is enabled; obtain from environment variable `IdentityBootstrap__Password` or .NET User Secrets. There is no password default. |
| `IdentitySecurity:SigningKeyBase64` | Existing JWT requirement; keep the local User Secrets signing key or another secure configuration provider. |
| `Database:CommandTimeoutSeconds`, `Database:MaxRetryCount` | Existing SQL command timeout/retry settings. |

Environment variables override JSON/User Secrets settings. Production automatic migration requires an explicit `DatabaseInitialization__MigrateOnStartup=true`; bootstrap independently requires explicit `IdentityBootstrap__Enabled=true`. Production operators should normally apply reviewed migration scripts with a deployment identity and leave runtime migration disabled.

To prepare local bootstrap credentials without displaying the password or placing it in command-line history, run this **only when intentional** in PowerShell. These instructions were not executed during implementation:

```powershell
$bootstrapCredential = Read-Host 'Bootstrap administrator password' -AsSecureString
$bootstrapPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($bootstrapCredential)
try {
    $bootstrapSettings = @{
        'IdentityBootstrap:Enabled' = 'true'
        'IdentityBootstrap:Password' = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bootstrapPointer)
    }
    $bootstrapSettings | ConvertTo-Json -Compress | dotnet user-secrets set --project MyOnlineShop
} finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bootstrapPointer)
    Remove-Variable bootstrapSettings -ErrorAction SilentlyContinue
    $bootstrapCredential.Dispose()
}
```

User Secrets is local development storage, not an encrypted production vault. Bootstrap password validation uses the supported Identity validators, required length 8 and the existing 8–128 character/no-whitespace/complexity policy. Missing or invalid passwords stop initialization before migration work. Do not run the application with Development migration enabled until the SQL target and migration permissions are approved. To start without any initialization, explicitly set both switches to `false`.

## Deterministic migration order and ownership

Every history is named `__EFMigrationsHistory` in its owner's schema. Each step resolves/disposes its own context scope and awaits `Database.MigrateAsync`. EF creates the target database when it is missing and the login has the necessary permissions; it applies pending migrations only.

| Order | DbContext | History schema | Owner project |
| --- | --- | --- | --- |
| 1 | FoundationDbContext | foundation | src/BuildingBlocks/Infrastructure |
| 2 | IdentityDbContext | identity | src/Modules/Identity/Infrastructure |
| 3 | CatalogDbContext | catalog | src/Modules/Catalog/Infrastructure |
| 4 | InventoryDbContext | inventory | src/Modules/Inventory/Infrastructure |
| 5 | CartDbContext | cart | src/Modules/Cart/Infrastructure |
| 6 | PricingDbContext | pricing | src/Modules/Pricing/Infrastructure |
| 7 | DiscountDbContext | discount | src/Modules/Discount/Infrastructure |
| 8 | MessagingDbContext | messaging | src/BuildingBlocks/Infrastructure |
| 9 | OrderDbContext | ordering | src/Modules/Order/Infrastructure |
| 10 | WalletDbContext | wallet | src/Modules/Wallet/Infrastructure |
| 11 | PaymentDbContext | payment | src/Modules/Payment/Infrastructure |
| 12 | RefundDbContext | refund | src/Modules/Refund/Infrastructure |
| 13 | ShippingDbContext | shipping | src/Modules/Shipping/Infrastructure |

This retains the existing documented module order. Reporting has no context. The coordinator lives in the API composition root; it does not grant modules access to each other's tables. The only new schema change is `20261011122924_AddUserNames`: nullable username/normalized-username columns and a filtered unique normalized-username index in Identity. Legacy email-only users remain intact; no account or password is seeded by a migration.

EF Core 10's SQL Server migration locking serializes concurrent migration attempts, and the existing Identity transaction-owned `sp_getapplock` serializes bootstrap attempts. These mechanisms are used within one configured database; they are not a distributed transaction or a substitute for coordinating incompatible application versions during deployments. See [EF migration guidance](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/applying).

## Administrator bootstrap behavior

When explicitly enabled, bootstrap runs only after every requested migration succeeds. It uses `UserManager<User>.CreateAsync(user, password)` through a custom `IUserPasswordStore<User>` over the existing module store. The existing ASP.NET Core password hasher and its 600,000-iteration setting are retained. Identity authentication continues to use the existing JWT and email login APIs; adding a username does not change those APIs or replace the existing tables. See [supported custom Identity storage](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/identity-custom-storage-providers).

The account is `zohrehkrm`, email `zohrehkarimi1372@gmail.com`, profile Zohreh Karimi. Requested phone `09370747630` is normalized to `+989370747630`, matching the existing E.164 input policy. The existing seeded `Administrator` role must already exist with its core `identity.users.manage` and `identity.roles.manage` permissions. Bootstrap adds no roles or permission grants.

An existing account is accepted only when username and email resolve to the same active user with the expected phone and a password hash. Email-only legacy accounts, username/email split across accounts, phone collisions, or inactive/profile-mismatched accounts fail safely. Bootstrap does not adopt or rewrite a different account. Repeated runs preserve the existing hash and security stamp; a missing Administrator assignment on an exactly matching account is restored once, audited, and invalidates its old access tokens. Creation, role assignment and audit share the existing Identity transaction. The bootstrap password is never overwritten on existing accounts, even when secure configuration changes.

While bootstrap remains enabled, a valid configured password remains required on each startup, including repeat runs. After provisioning, set `IdentityBootstrap:Enabled=false` and remove the bootstrap secret if desired. There is no HTTP bootstrap endpoint. The legacy `BootstrapIdentityAdmin=true` one-shot host switch now fails with guidance to use the explicit new configuration; the existing first-administrator application command remains available for established internal callers/tests.

## Failure and recovery

The first failed context stops subsequent contexts and prevents bootstrap and the web server/health endpoint from starting. Errors identify the stage/context and failure type; only dedicated safe bootstrap messages are included. Raw provider exceptions, connection strings and passwords are not attached to logged/rethrown startup messages. Cancellation is propagated. Already completed module migrations remain committed; fix the reported failure and restart so EF skips applied migrations and resumes safely. There is no automatic rollback of previously completed modules or reset of existing data.

Redis and RabbitMQ are independent existing features. Initialization resolves only SQL contexts and the Identity bootstrap port, and occurs before hosted workers start. Runtime migration disabled plus bootstrap enabled assumes reviewed schemas are already applied; an absent schema fails bootstrap rather than starting a partially configured API.

## SQL Server permissions

The Windows account running the process is the SQL login for the inspected local connection. It needs SQL Server login/connect access and a mapped user with database access. If the target does not exist, the migration identity needs an applicable database creation permission (`CREATE DATABASE`, `CREATE ANY DATABASE`, or `ALTER ANY DATABASE`), plus any provider-issued database-setting permissions required during creation. See [CREATE DATABASE permissions](https://learn.microsoft.com/en-us/sql/t-sql/statements/create-database-transact-sql).

For an existing target, reviewed migrations require creation of module schemas/tables, alteration of owner tables/indexes/constraints, appropriate `REFERENCES` permissions for foreign keys, and `SELECT`/`INSERT`/`UPDATE`/`DELETE` rights required by migration history and seed-data changes. This normally means `CREATE SCHEMA`, `CREATE TABLE`, and suitable `ALTER`/`CONTROL` and metadata visibility on the owner schemas/objects. A dedicated local migration identity with `db_owner` on this one development database is a sufficient broad development configuration; it does not imply permission to create a database at server scope. Bootstrap needs read/write access to Identity users, roles, permissions, links and audits, plus permission to obtain the application lock. Review exact least-privilege grants against the generated scripts and [SQL database permissions](https://learn.microsoft.com/en-us/sql/t-sql/statements/grant-database-permissions-transact-sql).

No permission grants are executed by application code. Production should separate the deployment/migration identity from the runtime login, which needs only the existing application DML/locking rights when runtime migration is disabled.

## Database-free verification

Test factories disable initialization explicitly. `scripts/Test-Regression.ps1` clears all SQL/Redis/RabbitMQ test connections and disables migration/bootstrap/messaging/refund/Redis features in its child process, restoring the caller's process settings afterward. Optional `-BuildOutputDirectory` keeps test builds separate from a running application, and `-NoRestore` uses already restored packages.

The focused tests cover repeat/parallel simulated bootstrap, duplicate prevention, missing/invalid random credentials, username/email conflicts, existing password preservation, seeded role checks, cancellation, context failure reporting/order, and all 13 owners' SQL Server migration metadata/current models. Real SQL creation, migration replay/locking, permission sufficiency and bootstrap rollback remain unverified until explicitly authorized SQL integration tests are run. No SQL Server, Docker, Redis or RabbitMQ was contacted or started in this implementation step.
