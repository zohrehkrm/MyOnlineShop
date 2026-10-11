# MyOnlineShop

ASP.NET Core .NET 10 modular monolith with module-owned SQL Server/EF Core persistence, JWT/permissions, CQRS, transactional Outbox/Inbox and optional Redis DTO caching.

Swagger UI is available in Development at `http://localhost:5039/swagger` (or `https://localhost:7147/swagger` with the HTTPS launch profile). Run `dotnet run --project MyOnlineShop --launch-profile http` after supplying the local configuration described in [DOCKER.md](DOCKER.md). The OpenAPI document is served at `/openapi/v1.json`. To try protected endpoints, call `POST /api/v1/identity/login`, copy the returned access token, and paste it into **Authorize** without the `Bearer` prefix. Swagger UI and the OpenAPI document are disabled outside Development.

* [Local Docker setup, configuration, migrations and troubleshooting](DOCKER.md)
* [Regression runner, security evidence and release blockers](HARDENING.md)
* [Architecture](ARCHITECTURE.md.txt) and [current project status](PROJECT_STATUS.md.txt)
* [Foundation/API conventions](FOUNDATION.md), [Identity](IDENTITY.md), [Catalog](CATALOG.md), [Inventory](INVENTORY.md), [Cart](CART.md)
* [Pricing/Discount](PRICING_DISCOUNT.md), [Order](ORDER.md), [Payment](PAYMENT.md), [Wallet](WALLET.md), [Messaging](MESSAGING.md), [Refund](REFUND.md), [Shipping](SHIPPING.md)
* [Caching](CACHING.md), [Reporting](REPORTING.md), [Admin/Warehouse APIs](ADMIN_WAREHOUSE.md)

Payment core/provider abstraction and Order/Inventory/Refund orchestration are implemented under simulated-provider validation. A real gateway adapter and live infrastructure/concurrency/E2E validation remain pending. Docker support is for local development; production readiness is not claimed.
