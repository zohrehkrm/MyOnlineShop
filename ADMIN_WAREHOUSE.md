# Administrative and warehouse APIs — Phase 15

Administrative capabilities stay in their existing modules. Controllers call the existing commands/queries; there is no second Admin module, inventory writer, calculation engine or report aggregator. Authentication and permission policies remain Identity-owned. Role names alone do not grant access. This phase adds no permission definitions, role grants, schema changes or migrations.

## API and permission map

Routes below are relative to `/api/v1`. Existing DTOs, `ApiResponse<T>` success envelopes, Problem Details errors and correlation IDs apply.

| Area | Routes and operations | Existing permission |
| --- | --- | --- |
| Catalog | `catalog/manage/products` and detail; create/update products, variants, categories, brands, attributes and values through existing Catalog routes | `catalog.manage` |
| Warehouses | GET `inventory/warehouses`, `inventory/warehouses/{id}` | `inventory.view` |
| Warehouse settings | POST/PUT warehouses; PUT `inventory/stocks/{stockId}/settings` | `inventory.adjust` |
| Stock and history | GET `inventory/stocks`, warehouse/variant stock, `inventory/movements`, `inventory/receipts`, **`inventory/adjustments`** | `inventory.view` |
| Receipts/returns | POST `inventory/receipts`, `inventory/returns` | `inventory.receive` |
| Adjustments | POST `inventory/adjustments` | `inventory.adjust` |
| Orders | GET `orders/management`, `orders/management/{id}` | `orders.view` |
| Order transitions | PATCH `orders/management/{id}/status`; supported cancellation uses `Status=Cancelled` | `orders.manage` |
| Shipping methods | Existing `shipping/management/methods` routes | `shipping.methods.manage` |
| Shipment reads | **GET `shipping/management/shipments`** and existing detail routes | `shipping.shipments.view` |
| Shipment preparation/state/tracking | Existing management creation/status/tracking routes | `shipping.shipments.manage` |
| Pricing | Existing price management/status/history routes | `pricing.manage` |
| Discounts/coupons | Existing discount management/status routes | `discount.manage` |
| Wallet credit | POST `admin/wallets/{userId}/credit` | `wallet.credit` |
| Reports | Existing Phase 14 GET `reports/*` routes | `reports.view` plus the existing scoped report permissions; Shipping reports also require shipment-view permission |

The wallet route's user ID selects an explicitly authorized credit recipient; the actor always comes from the authenticated principal. Customer Wallet reads remain owner-scoped. No arbitrary admin balance edit or customer-data endpoint was added. Inventory deduction remains a trusted internal contract, with no public deduction endpoint. Ordinary users receive no additional grants.

## New read capabilities

* Order management accepts `page`, `pageSize`, `status`, `fromUtc`, `toUtc`. The original route and pagination parameters remain compatible. Only exact named Order statuses are accepted, including rejection of numeric enum values. Existing detail DTOs supply immutable items and historical totals.
* Shipment management listing accepts the same pagination/date/status parameters, plus `orderId` and `shippingMethodId`. It returns `ShipmentPage` with summary DTOs: operational IDs, method name, status, tracking number/carrier, creation time and revision. Addresses and financial totals are omitted from this list. Existing authorized detail reads are unchanged.
* Adjustment history returns `InventoryPage<AdjustmentDto>` with adjustment ID and the existing movement DTO. It uses the existing Inventory query filters (warehouse, variant and movement type). `fromUtc` and `toUtc` also filter movements and receipts. Adjustment history excludes receipt/sale/return movements.

Pagination defaults to page 1/20 items, maximum 100 items. Invalid page/size/offset, malformed identifiers or invalid/reversed date ranges produce 400 errors. Dates normalize to UTC: FromUtc is inclusive and ToUtc exclusive. SQL counts, stable ordered Skip/Take and AsNoTracking DTO projections occur in the owning context. These operational lists accept optional dates without the bounded report-window policy used by Phase 14 reports. Separate count/page reads are not a cross-query transactionally consistent snapshot.

## Writes, audit and concurrency

Warehouse activation uses the existing WarehouseInput update. Stock settings configure activation/threshold only. Receipts, returns and manual/damage adjustments continue through the original atomic Inventory commands, operation-key idempotency, locking, conditional SQL updates, quantity rules and append-only movement/receipt/adjustment history. Existing SQL rowversion protection remains in place. Redis never supplies authoritative quantities.

Order and Shipping writes retain their state machines and existing audits. Administrators cannot mark an Order paid, skip fulfillment transitions, edit monetary snapshots or bypass cancellation rules. Shipping expected-revision checks still reject stale writes. Pricing recalculates through existing services; Discount validates eligibility/configuration; Wallet credit keeps existing transaction, ledger, audit and idempotency behavior.

Successful Catalog writes now use a module-local MVC action filter to record structured operation, authenticated actor GUID, target GUID, UTC timestamp and correlation ID. Warehouse create/update and stock settings log the same safe fields after successful command completion. Invalid actor claims are rejected before these writes. Logs contain no request body, product description, address, password, token or payment secret. Public Catalog GETs remain anonymous and are not mutation-audited.

These metadata audit records use the existing structured logging foundation. They are **not** a transactional database audit or outbox: persistence/retention depends on the configured logging sink, and a crash between commit and logging can omit a record. Existing durable quantity, financial and state-change audit records are retained. No separate audit subsystem or schema was introduced.

## Validation and dependencies

The affected API build and available tests cover endpoint policies, Catalog management, actor-safe audit, warehouse activation, receipt/adjustment rules, order filters/cancellation/invalid transitions, shipment filters/state/revision checks, permission separation for Pricing/Discount and Wallet, pagination, route uniqueness and offline SQL adjustment-query translation. EF InMemory tests validate query/application behavior only; they do not prove SQL atomicity, locking or rowversion behavior.

SQL Server, RabbitMQ and Redis tests stay compiled and skipped without their explicitly configured infrastructure. No service is installed, started or contacted, and no database or migration is applied. Live inventory/Wallet concurrency and transaction validation remain deferred. Phase 8 Payment is still absent: verified-payment producer/paid-Order integration, true Payment E2E tests, verified-payment reporting and production refund evidence remain pending. No Payment, refund, frontend, notifications or infrastructure deployment work is part of Phase 15.

Phase 16 — Testing & Hardening is the next recommended phase and requires a new request.
