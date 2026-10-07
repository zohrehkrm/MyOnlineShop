# Phase 14 — Reporting

Reporting is read-only and never owns business truth. It contains Contracts, Application query handlers, Infrastructure registration and Presentation controllers. A Domain project, command handlers and Reporting DbContext are unnecessary for these queries. It creates no reporting copies of business entities.

Each owning module implements an explicit DTO read port from Reporting.Contracts using its own DbContext. Reporting depends on those contracts, not module entities, repositories, contexts or tables. The only owner changes are these adapters, DI registrations and the Reporting contract reference. This dependency inversion keeps SQL ownership local and permits future remote implementations without exposing IQueryable across module boundaries. Historical sales never join current Catalog or Pricing data.

## API and permissions

All endpoints are GET `/api/v1/reports/{name}` and require authentication plus `reports.view`. They reuse existing API envelopes, correlation IDs, exception handling and Identity permission claims.

| Endpoint | Additional permissions | Data |
| --- | --- | --- |
| `dashboard` | `reports.sales`, `reports.inventory`, `reports.customers` | Order status counts, recorded sales by currency, customer registrations, current Catalog counts, current low-stock variant count |
| `sales` | `reports.sales` | Currency totals/mean order value and paged daily/monthly periods |
| `orders` | `reports.sales` | Status counts and paginated order ID/status/currency/value/creation time |
| `products` | `reports.sales` | Paged sales by historical OrderItem variant ID and currency, quantity, net item amount, distinct order frequency |
| `inventory` | `reports.inventory` | Paged current stock, low-stock variants and paged movement totals by warehouse/type |
| `customers` | `reports.customers`, `reports.sales` | Customer-role registration/activation counts and paged buyers/order value by ID and currency |
| `wallet` | `reports.financial` | Posted ledger credits, positive debit magnitudes, transaction count and InventoryRefund credit subtotal, by currency |
| `shipping` | `shipping.shipments.view` | Actual shipment status counts and paged counts by shipping method ID |

Role names alone never grant access. Dashboard combines permissions because it combines protected data. There are no customer-facing cross-account reports, writes, exports, cache endpoints or authorization bypasses. Buyer UUIDs are the only user identifiers exposed; reports do not return profiles, addresses, password hashes, sessions, tokens, secrets, descriptions or payment credentials.

## Filters and definitions

Common query parameters: `fromUtc`, `toUtc`, `currency`, `orderStatus`, `page`, `pageSize`. Omitted dates default to the previous 30 days ending at the injected UTC clock. Intervals are `[FromUtc, ToUtc)`; offsets normalize to UTC. Windows must be nonempty and at most 366 days. Page starts at 1, pageSize defaults to 20 and is bounded to 100; overflowing offsets are rejected. Status values use existing exact names, not numeric enum values. Unsupported filters produce 400 errors instead of silently changing report meaning.

```text
GET /api/v1/reports/sales?fromUtc=2026-10-01T00:00:00Z&toUtc=2026-11-01T00:00:00Z&currency=IRR&bucket=Month
GET /api/v1/reports/orders?orderStatus=Cancelled&page=1&pageSize=20
GET /api/v1/reports/inventory?warehouseId=<uuid>&lowStockOnly=true&pageSize=20
GET /api/v1/reports/shipping?shipmentStatus=Delivered&pageSize=20
```

* Sales represent immutable Order payable amounts whose **current** status is Paid, Processing, Shipped or Completed. Dates group/filter **order creation**, not payment settlement. Paid Order value includes historical shipping charges; item sales exclude them. A status filter further intersects these paid statuses. Current Catalog names, categories and current price changes do not alter sales.
* Currency is always retained in every monetary grouping. There is no FX conversion or cross-currency grand total. Average order value is a decimal arithmetic mean. Buyers rank within currency by recorded order value; variants rank by quantity with stable variant/currency tie-breakers.
* Sales `bucket=Day` or `Month` returns UTC year/month/day components; monthly Day is zero. Timeline, buyers, variant results, detailed orders, stock, movements and method summaries are paginated, including total group/row counts. Sales currency totals and status summaries have naturally bounded cardinality.
* Customer registrations count users created in the window who currently hold the seeded Customer role. Active counts use the current activation state. These counts are separate from buyer totals: buyers are actual Order owners in the creation-date cohort, including previous/other role assignments. Currency/status filters affect Order spending, not registration counts.
* Catalog counts describe current Product records and current Active status, not registration-date cohorts or guaranteed purchasability. Inventory stock is current, not a historical stock snapshot. Movement dates use movement creation UTC time. Low-stock count is distinct variants with at least one active stock row in an active warehouse at/below its own threshold; it is not global stock shortage or a reservation/availability guarantee. `warehouseId` narrows both inventory parts; `lowStockOnly` narrows current stock only. Inventory rejects currency/order-status filters.
* Wallet reports use only posted ledger entries in the date range and currency. Debits are positive magnitudes of signed negative entries. InventoryRefund is the exact reference used by existing Refund processing; the subtotal is included in credits and must not be added again. It is not a total of pending refunds or all future refund types. Wallet rejects orderStatus.
* Shipping filters shipment creation time/currency plus optional `shipmentStatus`; it rejects orderStatus. Method counts use persisted method IDs. Shipments are never modified and no delivery/payment relationship is fabricated.

The response is an administrative read assembled from independent queries, not a cross-module point-in-time snapshot. Concurrent writes can change counts between queries/pages. Existing owners remain authoritative for operational and financial decisions.

## Unavailable reports

Phase 8 Payment is absent. There is no `/payments` endpoint, successful/failed/pending payment count, gateway breakdown, verified receipt total or payment-status filter. Dashboard explicitly reports `PaymentReportsAvailable=false` and labels the Order-based sales basis. Payment was not implemented or simulated.

OrderItem snapshots contain variant ID, SKU/name and historical money but no historical parent ProductId/CategoryId. Product sales are therefore reported at variant granularity. Parent/category sales are deferred rather than joined to mutable current category assignments. There is no refunded Order status or complete refund ledger association supporting refunded-order counts; those counts are unavailable. Delivery-duration analytics, historical stock snapshots and exports are not part of this implementation.

## Queries, performance and migrations

All owner adapters use AsNoTracking, database-side WHERE/GROUP BY/COUNT_BIG/SUM/AVG and DTO projections. They do not materialize complete orders/items/ledger tables before grouping, use Include, issue per-row queries or return EF entities. Stable ordering precedes Skip/Take. All asynchronous EF calls receive cancellation tokens. Redis is bypassed for every report.

Existing indexes remain: Order status/creation and owner/creation, OrderItem order/variant, Inventory warehouse/variant and movement stock/type/creation, Identity role relationships, Wallet owner/creation, and Shipping status/creation and owner/creation. No speculative performance indexes were added without workload evidence. Global date-filtered ledger/movement/customer reports may still need scan/plan tuning on realistic SQL datasets; the bounded date window limits the report scope, not a promise of a particular execution plan. Profile before adding owner-managed indexes.

`AddReportingPermissions` is an incremental Identity migration adding only five permission rows and grants to the existing administrator role. The model snapshot was updated. The forward SQL is generated in `artifacts/Phase14ReportingPermissions.sql` for review. No reporting business tables, duplicate data, index changes or schema redesign are introduced. No migrations were applied; deploy the permission migration through the normal future authorized database workflow before relying on seeded administrator grants.

## Validation and deferred infrastructure

Database-free tests use explicit EF InMemory fixtures, real owner query adapters/handlers, API permission policies and offline SQL Server query generation. They cover empty data, dashboard/sales/status/variant/customer/inventory/wallet/shipping aggregation, historical money, currency separation, date boundaries, offsets, monthly buckets, pagination, validation, privacy, permission isolation, envelopes and correlation. Fixture paid Orders and wallet postings are trusted test data, not a fake Payment producer.

Two compiled `ReportingSqlFact` tests are skipped unless `REPORTING_TEST_SQL_SERVER` is explicitly supplied later. They read an already migrated test database; they never create/reset/apply migrations or seed persistent data. Offline translation tests compile aggregate and pagination SQL without opening a connection. Identity metadata tests verify migration/model agreement and administrator-only grants. Live SQL execution plans, isolation/concurrency behavior and verified Payment reporting remain deferred. No SQL Server, LocalDB, RabbitMQ, Redis or Docker is installed, started or required for normal tests.
