# MyOnlineShop

ASP.NET Core .NET 10 modular monolith with module-owned SQL Server/EF Core persistence, JWT/permissions, CQRS, transactional Outbox/Inbox and optional Redis DTO caching.

Swagger UI is available in Development at `http://localhost:5039/swagger` (or `https://localhost:7147/swagger` with the HTTPS launch profile). Run `dotnet run --project MyOnlineShop --launch-profile http` after supplying the local configuration described in [DOCKER.md](DOCKER.md). The OpenAPI document is served at `/openapi/v1.json`. To try protected endpoints, call `POST /api/v1/identity/login`, copy the returned access token, and paste it into **Authorize** without the `Bearer` prefix. Swagger UI and the OpenAPI document are disabled outside Development.

* [Local Docker setup, configuration, migrations and troubleshooting](DOCKER.md)
* [Regression runner, security evidence and release blockers](HARDENING.md)
* [Architecture](ARCHITECTURE.md.txt) and [current project status](PROJECT_STATUS.md)
* [Database initialization, migration order and default administrator bootstrap](DATABASE_INITIALIZATION.md)
* [Foundation/API conventions](FOUNDATION.md), [Identity](IDENTITY.md), [Catalog](CATALOG.md), [Inventory](INVENTORY.md), [Cart](CART.md)
* [Pricing/Discount](PRICING_DISCOUNT.md), [Order](ORDER.md), [Payment](PAYMENT.md), [Wallet](WALLET.md), [Messaging](MESSAGING.md), [Refund](REFUND.md), [Shipping](SHIPPING.md)
* [Caching](CACHING.md), [Reporting](REPORTING.md), [Admin/Warehouse APIs](ADMIN_WAREHOUSE.md)

For native Windows development, `MyOnlineShop/appsettings.Development.json` connects to the local default SQL Server instance (`localhost`) and database `MyOnlineShop` using Windows Authentication (`Integrated Security=True`). SQL Server uses the Windows account running the API; that account needs access to the database. For SQL Express, change the JSON server value to `localhost\\SQLEXPRESS`; for LocalDB, use `(localdb)\\MSSQLLocalDB`. Environment variable `ConnectionStrings__SqlServer` overrides this setting, including for Docker. The trusted certificate setting is for local development.

Development loads a persistent JWT signing key from .NET User Secrets (`MyOnlineShop-LocalDevelopment`). On a new machine, generate and store it once using Windows PowerShell or PowerShell 7:

```powershell
$jwtBytes = New-Object byte[] 32
$jwtGenerator = [System.Security.Cryptography.RandomNumberGenerator]::Create()
try { $jwtGenerator.GetBytes($jwtBytes) } finally { $jwtGenerator.Dispose() }
@{ 'IdentitySecurity:SigningKeyBase64' = [Convert]::ToBase64String($jwtBytes) } | ConvertTo-Json -Compress | dotnet user-secrets set --project MyOnlineShop
dotnet run --project MyOnlineShop --launch-profile http
```

Generate the key only once; replacing it invalidates earlier tokens. Environment variable `IdentitySecurity__SigningKeyBase64` overrides User Secrets. Development startup now applies pending module migrations by default; set `DatabaseInitialization__MigrateOnStartup=false` to disable this. See [database initialization](DATABASE_INITIALIZATION.md) before starting against an existing database. Administrator bootstrap is a separate explicit opt-in; it has no default password.

Payment core/provider abstraction and Order/Inventory/Refund orchestration are implemented under simulated-provider validation. A real gateway adapter and live infrastructure/concurrency/E2E validation remain pending. Docker support is for local development; production readiness is not claimed.

API errors default to Persian (`fa-IR`). Select English with `Accept-Language: en-US` or `?culture=en-US`; supported cultures are `fa`, `fa-IR`, `en`, and `en-US`, and unsupported languages fall back to Persian. Responses include `Content-Language`. Error resources live in `src/BuildingBlocks/Abstractions/Localization`: `ValidationMessages` for field rules and names, `HttpErrors` for HTTP failures, and `ApplicationErrors` for module error codes. The base `.resx` files contain Persian and `.en.resx` files contain English; new error codes should be added to both. Error codes, field keys, and correlation IDs remain stable across cultures.
