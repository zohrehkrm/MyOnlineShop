# MyOnlineShop

ASP.NET Core .NET 10 modular monolith with module-owned SQL Server/EF Core persistence, JWT/permissions, CQRS, transactional Outbox/Inbox and optional Redis DTO caching.

* [Local Docker setup, configuration, migrations and troubleshooting](DOCKER.md)
* [Regression runner, security evidence and release blockers](HARDENING.md)
* [Architecture](ARCHITECTURE.md.txt) and [current project status](PROJECT_STATUS.md.txt)
* [Foundation/API conventions](FOUNDATION.md), [Identity](IDENTITY.md), [Catalog](CATALOG.md), [Inventory](INVENTORY.md), [Cart](CART.md)
* [Pricing/Discount](PRICING_DISCOUNT.md), [Order](ORDER.md), [Wallet](WALLET.md), [Messaging](MESSAGING.md), [Refund](REFUND.md), [Shipping](SHIPPING.md)
* [Caching](CACHING.md), [Reporting](REPORTING.md), [Admin/Warehouse APIs](ADMIN_WAREHOUSE.md)

Phase 8 Payment is incomplete. Verified-payment integration, authoritative production refund evidence and live infrastructure/concurrency validation remain pending. Docker support is for local development; production readiness is not claimed.
