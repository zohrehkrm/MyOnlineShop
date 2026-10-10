# Local Docker development

Phase 17 supplies configuration and instructions only. No containers were started, images built, services contacted or migrations applied during validation. Phase 8 Payment is still absent; this environment cannot demonstrate verified Payment-to-Inventory/Order/Refund end-to-end behavior. See [HARDENING.md](HARDENING.md) for release blockers and [README.md](README.md) for module documentation.

## Prerequisites and images

Use the .NET **10 SDK**, Docker Desktop in Linux-container mode or a compatible Linux Docker Engine, and Docker Compose v2. The API and EF packages target .NET 10; the local EF tool in `.config/dotnet-tools.json` is 10.0.0. SQL Server needs an x86-64 Linux environment and at least 2 GiB RAM, with additional memory for the other services. ARM/emulation is not validated here.

The multi-stage Dockerfile uses the official [SDK/ASP.NET 10 images](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/docker/building-net-docker-images?view=aspnetcore-10.0), publishes the existing API project, and runs as the image's non-root application user. Curl supplies its process-liveness check. Compose uses [SQL Server 2022 Developer](https://learn.microsoft.com/en-us/sql/linux/sql-server-linux-docker-container-deployment?view=sql-server-ver17), [Redis 7.4 Alpine](https://hub.docker.com/_/redis/) and [RabbitMQ 4.2 management Alpine](https://hub.docker.com/_/rabbitmq/). These moving development tags need reviewed, pinned digests for reproducible deployment. Developer SQL Server licensing is for development/testing.

## Local secrets

From the repository root, create local configuration:

```powershell
powershell -NoProfile -File scripts/Initialize-LocalEnvironment.ps1
docker compose config --quiet
```

The script generates independent cryptographic secrets and a 32-byte Base64 JWT key, writes `.env`, and refuses to overwrite an existing file. It starts no service. Your existing PowerShell execution policy must permit the script; no policy change is required by this repository. Alternatively, copy `.env.example` to `.env` and supply your own values. All required placeholders in the example are empty.

| Required variable | Purpose |
| --- | --- |
| `MSSQL_SA_PASSWORD` | Strong local SQL administrator password; use at least three character classes |
| `API_SQL_CONNECTION_STRING` | Container connection: `Server=sqlserver,1433;Database=MyOnlineShop;User ID=sa;Password=<local password>;Encrypt=True;TrustServerCertificate=True;MultipleActiveResultSets=False` |
| `JWT_SIGNING_KEY_BASE64` | Base64 encoding of at least 32 cryptographically random bytes |
| `REDIS_PASSWORD` | Redis authentication; generated hexadecimal values avoid connection-string delimiter escaping |
| `RABBITMQ_USERNAME` | Dedicated local broker user, other than `guest` |
| `RABBITMQ_PASSWORD` | Strong independent broker password |

Keep the SQL password and API connection string consistent. Quote dotenv values containing special characters; the generator uses single quotes. Protect `.env` and do not share expanded Compose configuration, container inspection output or secret-bearing logs. `.env` variants are excluded by `.gitignore` and `.dockerignore`, with only `.env.example` permitted in source control. Environment-specific appsettings files are excluded from the image except the inspected safe Development file. Secrets remain visible to local Docker administrators: this is not a production secret store.

Optional variables: `API_PORT=8080`, `SQL_PORT=14333`, `RABBITMQ_MANAGEMENT_PORT=15672`, `ASPNETCORE_ENVIRONMENT=Development`, `REDIS_ENABLED=true`, `MESSAGING_ENABLED=false`. Compose translates them to the application's existing `ConnectionStrings__SqlServer`, `IdentitySecurity__SigningKeyBase64`, `Redis__*` and `Messaging__RabbitMq__*` settings. Existing JWT issuer/audience, JSON logging, validation and authentication remain unchanged. Missing required Compose values fail configuration resolution; existing application option validation remains active.

## Manual startup and shutdown

The following commands are for a developer intentionally starting local infrastructure. They were **not executed** in Phase 17. An agent must obtain explicit approval before starting services or modifying a database.

```powershell
docker compose up -d sqlserver redis rabbitmq
docker compose ps
# Apply the reviewed migrations below before starting the API.
docker compose up -d --build api
```

SQL health checks use `/opt/mssql-tools18/bin/sqlcmd` and environment-based credentials. Redis checks authenticated PING; RabbitMQ checks its node. API startup waits for all three services to be healthy. Its `/api/v1/health` endpoint checks process liveness only: a healthy response does not establish that migrations, database access or business flows work.

Swagger is at `http://localhost:8080/swagger/index.html`, OpenAPI at `/openapi/v1.json`, and liveness at `/api/v1/health`; change the port to match `API_PORT`. Swagger/OpenAPI are enabled only in Development. Local Compose uses HTTP and trusts the SQL container's development certificate. Existing HTTPS redirection is retained; without a configured HTTPS endpoint it may log that the HTTPS port cannot be determined. Production requires intentional TLS/proxy configuration and verification.

```powershell
docker compose stop
# Or remove the containers/network while retaining named data volumes:
docker compose down
```

Do not use volume-removal options or prune/delete the named volumes. `sql-data`, `redis-data` and `rabbitmq-data` retain state across container recreation. Keep the project name `myonlineshop-dev` stable to retain the same volume names. Changing initial SQL/RabbitMQ credentials in `.env` does not rotate credentials already stored in their volumes; use the service's intentional credential-management procedure.

## Connections and optional messaging

| Service | API/container address | Host access by default |
| --- | --- | --- |
| API | `api:8080` | `127.0.0.1:8080` |
| SQL Server | `sqlserver,1433` | `localhost,14333` in SSMS/sqlcmd |
| Redis | `redis:6379` with password | None |
| RabbitMQ AMQP | `rabbitmq:5672`, vhost `myonlineshop-dev` | None |
| RabbitMQ management | `rabbitmq:15672` | None; optional override below |

Published ports bind to localhost. Services share an internal backend network; Redis/AMQP stay private. The local API uses `sa` only for development convenience. Production requires a scoped application login and a separate migration identity. Local SQL connections use encryption with `TrustServerCertificate=True`; validate certificates properly outside development. Keep `MultipleActiveResultSets=False` for existing transactional savepoint behavior.

To enable the management UI intentionally:

```powershell
docker compose -f compose.yaml -f compose.management.yaml up -d rabbitmq
```

Visit `http://localhost:15672` with the configured non-guest credentials. Use the same two files for subsequent Compose commands managing that override. The RabbitMQ hostname stays stable for persistent node identity. The UI override publishes no AMQP or Redis port.

After applying Messaging and participant migrations, set `MESSAGING_ENABLED=true` and recreate the API intentionally to enable the existing publisher/consumers. Settings bind the existing Inventory payment-success, Refund inventory-unavailable and Refund-due consumers, plus their dead-letter queues. `operational.audit.v1` durably routes existing Order/Refund events without inventing a new business consumer; monitor retention because it can accumulate messages. Topology is provisioned by the existing messaging implementation when enabled. Workers are disabled by default. `RefundProcessing__Enabled` and `BootstrapIdentityAdmin` are always false in this Compose configuration; the authoritative Payment/refund-evidence implementation remains pending. See [MESSAGING.md](MESSAGING.md), [REFUND.md](REFUND.md) and [IDENTITY.md](IDENTITY.md) before enabling/provisioning anything separately.

Redis remains optional DTO caching. Set `REDIS_ENABLED=false` to disable API caching. Existing bounded timeout/failure fallback remains intact after startup; initial Compose startup still waits for Redis health even if caching is disabled. SQL remains authoritative for money, stock and checkout calculations. For a later authorized authenticated check:

```powershell
docker compose exec redis sh -c 'REDISCLI_AUTH="$REDIS_PASSWORD" redis-cli ping'
```

See [CACHING.md](CACHING.md) for expiry/invalidation/fallback guarantees. Do not flush Redis to troubleshoot application state.

## EF Core and database setup

No automatic startup migration or database reset is configured. All contexts use the same configured SQL Server database, with module-owned schemas and migration histories. Compose health establishes instance availability, not schema readiness.

**Host tooling does not load `.env`.** Set the following process environment values securely in the terminal used for EF tooling, without displaying them:

* `ConnectionStrings__SqlServer`: host connection using `Server=localhost,14333` (or `SQL_PORT`), the intended database and matching credentials; keep the other SQL options above.
* `IdentitySecurity__SigningKeyBase64`: the generated local JWT key.
* `Messaging__Enabled=false`, `RefundProcessing__Enabled=false`, `Redis__Enabled=false`, `BootstrapIdentityAdmin=false`.

Do not use the container hostname `sqlserver` from the host. Do not evaluate dotenv content as PowerShell code. Use a secure configuration provider or copy the relevant values into process settings locally; avoid placing secrets in command-line arguments/history. Restore/remove process settings when finished.

```powershell
dotnet tool restore
dotnet build MyOnlineShop/MyOnlineShop.csproj
# Example: generate a reviewable script without connecting to SQL Server.
dotnet ef migrations script --idempotent --project src/Modules/Identity/Infrastructure --startup-project MyOnlineShop --context IdentityDbContext --output artifacts/Identity.sql
# Only when intentional and authorized, apply the reviewed migration:
dotnet ef database update --project src/Modules/Identity/Infrastructure --startup-project MyOnlineShop --context IdentityDbContext
```

Create the `artifacts` directory if needed before script generation. Applying migrations can create the configured development database on the first update and subsequently changes its schema/data. Confirm the endpoint/database, inspect generated `Up`/`Down` and forward SQL, back up persistent data, and apply each owner context intentionally. Never delete/recreate the database, existing migrations or histories to repair a failure. Provision an administrator separately using the existing [Identity bootstrap workflow](IDENTITY.md), after Identity migrations; no default account is created by Docker.

Use this explicit owner/context order for a new local environment (no Payment or Reporting database context exists):

| Context | `--project` |
| --- | --- |
| `FoundationDbContext` | `src/BuildingBlocks/Infrastructure` |
| `IdentityDbContext` | `src/Modules/Identity/Infrastructure` |
| `CatalogDbContext` | `src/Modules/Catalog/Infrastructure` |
| `InventoryDbContext` | `src/Modules/Inventory/Infrastructure` |
| `CartDbContext` | `src/Modules/Cart/Infrastructure` |
| `PricingDbContext` | `src/Modules/Pricing/Infrastructure` |
| `DiscountDbContext` | `src/Modules/Discount/Infrastructure` |
| `MessagingDbContext` | `src/BuildingBlocks/Infrastructure` |
| `OrderDbContext` | `src/Modules/Order/Infrastructure` |
| `WalletDbContext` | `src/Modules/Wallet/Infrastructure` |
| `RefundDbContext` | `src/Modules/Refund/Infrastructure` |
| `ShippingDbContext` | `src/Modules/Shipping/Infrastructure` |

Repeat the reviewed script/update command with each row's project and context, always using `--startup-project MyOnlineShop`. Reporting reads through owner contracts and has no separate schema. For a future intentional Inventory model change, create a migration as follows, then review its mappings, snapshot and generated SQL before applying:

```powershell
dotnet ef migrations add DescriptiveChangeName --project src/Modules/Inventory/Infrastructure --startup-project MyOnlineShop --context InventoryDbContext --output-dir Persistence/Migrations
```

Phase 17 changes no EF models and requires no new migration. To run the API on the host instead of in Docker, use the same host process settings above and `dotnet run --project MyOnlineShop --launch-profile http` (HTTP port 5039). Leave host Redis/messaging disabled because their ports are not published. See [FOUNDATION.md](FOUNDATION.md) for existing launch profiles/API conventions.

## Tests

Run all available database-free regression suites without starting dependencies:

```powershell
powershell -NoProfile -File scripts/Test-Regression.ps1 -ResultsDirectory artifacts/local-tests
```

The existing runner clears infrastructure opt-in variables, disables external features/bootstrap, builds test projects serially and records TRX/accurate skip counts. See [HARDENING.md](HARDENING.md) for the 13 suites and deferred cases. Phase 17 reran only Foundation/Identity, which cover the affected host configuration/authentication surface: **49 passed, 0 failed, 14 SQL-dependent skips**, recorded in `artifacts/phase17-tests`. The API build passed with zero warnings/errors. Both Compose configurations, static Dockerfile/PowerShell checks, ports/volumes, empty secret placeholders and documentation links passed validation. The generator was not run to create real secrets, and no image was built or run. Unrelated module tests were not repeated.

Future infrastructure tests need explicit approval and a disposable test endpoint. SQL suite opt-in variables include `FOUNDATION_TEST_SQL_SERVER`, `IDENTITY_TEST_SQL_SERVER`, `INVENTORY_TEST_SQL_SERVER`, `CART_TEST_SQL_SERVER`, `PRICING_TEST_SQL_SERVER`, `ORDER_TEST_SQL_SERVER`, `WALLET_TEST_SQL_SERVER`, `MESSAGING_TEST_SQL_SERVER`, `REFUND_TEST_SQL_SERVER`, `SHIPPING_TEST_SQL_SERVER` and `REPORTING_TEST_SQL_SERVER`. Run the relevant project with `dotnet test tests/MyOnlineShop.<Area>.Tests` only after reviewing its fixture. Most SQL fixtures create/migrate/delete their own GUID-named database and must not target production; Foundation performs a read-only connectivity check and Reporting expects a separately prepared fully migrated database. Do not use LocalDB or reset the application database.

RabbitMQ uses `MESSAGING_TEST_RABBITMQ` JSON as documented in [MESSAGING.md](MESSAGING.md); Redis uses `REDIS_TEST_CONNECTION_STRING` as documented in [CACHING.md](CACHING.md). Host integration tests require an intentional localhost-only AMQP/Redis port override or execution within the Docker network; the management override does not provide that connectivity. Do not expose those ports publicly or reuse a production broker namespace. The database-free regression runner deliberately clears these values, so run opted-in integration suites separately. True Payment E2E cases remain blocked until Phase 8 is implemented.

## Troubleshooting

* **Port conflict:** change `API_PORT`, `SQL_PORT` or optional management port in `.env`, then use that port in host URLs/connections. Do not remove data volumes.
* **Unhealthy SQL:** allow its startup grace period, check available memory and password complexity, and inspect SQL logs locally without sharing secrets. A persisted volume retains its old credentials. The health check requires the documented `mssql-tools18` image path.
* **Database errors:** distinguish `sqlserver,1433` inside Docker from `localhost,14333` on the host, verify the intended database and manually applied owner migrations, and keep MARS disabled. API liveness does not test SQL.
* **Missing configuration:** run `docker compose config --quiet`; required empty placeholders fail clearly. Host .NET tooling needs its own environment settings. JWT key must be valid Base64 with at least 32 bytes; authentication is not bypassed.
* **RabbitMQ:** check the configured non-guest user, existing persisted credentials, vhost and private hostname; host AMQP access is disabled. Enable workers intentionally only after migrations. Inspect dead-letter queues and existing retry diagnostics without discarding messages.
* **Redis:** verify password/private hostname and Redis health. Startup health gating differs from the existing runtime cache fallback. Cache failures must not change financial/stock authority.
* **EF errors:** supply `--context`, the owning Infrastructure project and the API startup project; restore the pinned tool/build first. Review the migration and endpoint rather than deleting migration history or data.
* **API image:** no image was built/run in this phase; first build requires registry/NuGet/package access for SDK dependencies and curl. CLI/Compose syntax checks do not establish runtime container health. Docker client config-access warnings may be machine-specific; do not publish its credential file.

## Production work remaining

This configuration is local development only. Production needs a reviewed secret provider/rotation, TLS and trusted certificates, a reverse proxy with validated forwarded-header/HTTPS behavior, least-privilege SQL access, tested backups/recovery, durable audit/log retention, monitoring/alerting, rate limiting, resource limits, network restrictions, broker durability/recovery/dead-message operations, a controlled migration rollout and image/security review. None is claimed fully configured. No Kubernetes or cloud platform is introduced.

Phase 8 Payment producer/verification, authoritative refund evidence, true Payment E2E and live SQL concurrency/rollback, broker confirms/recovery and Redis validation remain incomplete. Docker configuration does not resolve those release blockers.
