# Shipping — Phase 12

Shipping owns methods, server-side cost calculation, immutable shipment/address snapshots, tracking, status and transactional audit. It follows the existing Domain/Contracts/Application/Infrastructure/Presentation structure, CQRS command/read interfaces, SQL Server EF Code First, Foundation errors/envelopes/correlation and Identity permission policies.

Shipping has no Payment, Wallet, Refund, Notification or Reporting dependency. Inventory availability remains in the existing checkout flow; Shipping never reserves/deducts stock or clears Cart. No external carriers, Redis, Docker or new messaging infrastructure/events are implemented.

## Address and method rules

The shipping address includes recipient, international phone (`+countrycode` format), state/province, city, street, postal code, two-letter country code and optional building/unit. These are shipment/checkout fields only; Identity registration requirements remain unchanged.

Shipping methods have stable ID, normalized unique code, name, optional description, decimal BaseCost, explicit currency, active flag and RequiresTracking configuration. Names/codes are generic; there is no hardcoded Standard/Express/Pickup pricing branch. A configured collection method may have zero cost and RequiresTracking=false. Management updates require the current GUID Revision and are protected additionally by SQL rowversion. Methods are deactivated rather than deleted.

The centralized `IShippingQuotes`/ShippingCost calculator currently applies the active method's flat BaseCost in its own currency. It validates address and money using existing Pricing.Contracts rules. It is the single extension point for future shipping rules; no external carrier rate lookup is performed. Client Cost/ShippingCost fields are not input authority.

## Checkout and historical totals

Existing checkout now accepts optional `Shipping` selection:

```json
{
  "idempotencyKey": "<new-guid>",
  "currency": "IRR",
  "shipping": {
    "shippingMethodId": "<active-method-guid>",
    "address": {
      "recipient": "Recipient Name",
      "phoneNumber": "+989123456789",
      "state": "Tehran",
      "city": "Tehran",
      "street": "Street address",
      "postalCode": "1234567890",
      "countryCode": "IR",
      "building": "Building A",
      "unit": "2"
    }
  }
}
```

Checkout still validates the server-owned Cart, Catalog, Pricing/Discount and advisory Inventory availability. For a physical order with selection, it obtains a fresh server quote and stores method ID/code/name, currency, cost, tracking requirement, quote time and full address as an immutable Order-owned snapshot. Customer requests cannot supply authoritative cost/owner. Digital-only orders reject shipping selection. Supplying both legacy Address and Shipping is rejected to avoid ambiguous addresses.

Order totals now follow `PayableAmount = Subtotal - DiscountTotal + ShippingCost`. Subtotal/discounts remain merchandise-only; ShippingCost is not discounted by existing merchandise rules. Existing currency precision and money bounds apply to the full payable total. OrderCreated's existing Outbox event carries that authoritative full amount. Order/Cart/Outbox transactions and idempotency behavior are reused; no checkout redesign or new Shipping transaction participant is needed because selection is snapshotted in Order itself.

Existing orders gain ShippingCost=0 and nullable new snapshot columns, retaining their original purchase data and totals. Requests without Shipping retain their pre-phase request fingerprint, optional legacy Address and previous behavior. Requests with Shipping include that selection in the fingerprint. A replay returns the original persisted method/cost/address even after method pricing changes; changed selection with the same key conflicts.

Compatibility limitation: legacy/unselected orders lack a complete shipping quote/address and cannot be fulfilled through new Shipment creation. Their historical data is not invented, altered or repriced. A future legacy fulfillment/backfill policy needs explicit authorization. To use Phase 12 fulfillment, physical checkout must supply Shipping. Address books/profile changes are not shipment sources of truth.

## Order association and status

Shipping uses a small read-only `IOrderShippingSnapshots` contract. It never accesses Order entities/repositories/tables directly and does not mutate Order status. Creation requires an existing paid/Processing physical Order with captured Shipping. Current method must still exist and be active; its currency must match the captured quote. The shipment copies the historical quoted cost/name/code/address, not the current price or user profile.

There is one shipment per Order, enforced with a unique OrderId index. Repeated creation returns the existing shipment; competing inserts may receive a safe 409 and can reload/retry. Multi-package/split fulfillment is deferred. Shipping method references are module-local restrictive foreign keys; Order/user references remain IDs across module boundaries.

Allowed transitions:

```text
Pending -> Preparing -> Shipped -> InTransit -> Delivered
   |           |
   +-----------+-> Cancelled
```

Delivered/Cancelled are terminal. Carrier/tracking are assigned or corrected only before dispatch and become immutable afterward. RequiresTracking=true demands both before Shipped; false supports configured collection/custom methods. ShippedAtUtc/DeliveredAtUtc are server timestamps. Exact same-status requests using the current revision are harmless reads of the unchanged state; stale revision updates return 409. SQL rowversion also protects simultaneous writes. No distributed locking is introduced.

Cancellation is logistics-only: no refund, inventory restocking or financial operation is triggered.

Paid Order association is revalidated for fulfillment transitions. Shipping state and Order lifecycle stay separately owned; no automatic Order Shipped/Completed synchronization is invented in this phase.

Phase 8 remains absent. Therefore true verified Payment -> paid Order -> Shipment end-to-end verification is blocked. Tests use explicit trusted paid Order domain fixtures; there is no fake production payment or new endpoint that marks an Order Paid. Methods/quotes and checkout selection work independently of Payment.

## API and permission boundaries

All routes use existing authenticated API envelopes/Problem Details and cancellation/correlation support.

| Route | Authority |
| --- | --- |
| GET /api/v1/shipping/methods?currency=IRR | Authenticated customer; active methods for currency |
| POST /api/v1/shipping/quotes | Authenticated customer; informational server-calculated quote |
| GET /api/v1/shipping/orders/{orderId}/shipment | Authenticated Order owner only |
| GET /api/v1/shipping/management/methods | shipping.methods.manage |
| POST /api/v1/shipping/management/methods | shipping.methods.manage |
| PUT /api/v1/shipping/management/methods/{id} | shipping.methods.manage; ExpectedRevision + Method |
| POST /api/v1/shipping/management/orders/{orderId}/shipment | shipping.shipments.manage; no caller cost/address/owner authority |
| GET /api/v1/shipping/management/shipments/{id} | shipping.shipments.view |
| PATCH /api/v1/shipping/management/shipments/{id}/status | shipping.shipments.manage; ExpectedRevision + Status |
| PUT /api/v1/shipping/management/shipments/{id}/tracking | shipping.shipments.manage; ExpectedRevision + TrackingNumber + Carrier |

Cross-owner customer reads return 404, and management endpoints require explicit permission claims; role name alone does not bypass policy. Three permissions and their Administrator grants are added through an incremental Identity metadata migration without redesigning Identity.

Shipping audit stores entity/actor IDs, action, UTC time and correlation in the same transaction as the change. Safe structured operation logs contain identifiers/action only, not recipient/phone/address/tracking payloads or payment secrets. EF protects shipment purchase/address snapshots and audit from edits/deletion. Methods remain editable configuration while existing Order/Shipment snapshots remain historical.

## Migrations and validation

Generated/reviewed offline:

* InitialShipping: owned shipping schema, Methods/Shipments/Audit, unique codes/OrderId, rowversions, restrictive method relationship, decimal amounts and state/currency/tracking/timestamp constraints.
* AddOrderShipping: additive nullable shipping snapshot columns and zero-default ShippingCost; replaces the Order totals constraint to include shipping. No existing financial rows/totals are rewritten.
* AddShippingPermissions: only three permissions and corresponding Administrator grants.

InitialShipping/AddOrderShipping downgrade refuses destructive historical snapshot/audit removal. Earlier migrations remain unchanged. Review scripts are `artifacts/shipping-migration.sql`, `artifacts/order-shipping-migration.sql` and `artifacts/shipping-permissions.sql`.

Available Shipping tests exercise methods/currency/cost/precision, checkout totals and replay after configuration changes, snapshots, paid/physical association, owner isolation, inactive/missing methods/orders, tracking, legal/illegal/terminal transitions, duplicate/stale revision updates, immutable history and HTTP authorization/ignored client money. Offline SQL model/migration/translation tests open no database connection. InMemory/unit-of-work and authentication substitutes do not prove live SQL atomicity/concurrency or real JWT behavior; existing Identity tests provide separate authentication regressions.

Five compiled SQL scenarios are explicitly skipped without `SHIPPING_TEST_SQL_SERVER`: Order/Shipment snapshot persistence, concurrent creation/unique Order relation, concurrent revision transitions and audit, rollback after flushed shipment/audit, and native rowversion/history guards. A future authorized run uses only a checked GUID-named disposable test database and rejects LocalDB. True Payment fulfillment end-to-end is separately blocked by Phase 8.

No SQL Server/LocalDB/RabbitMQ connection, migration application, persistent database modification, service installation or container operation is performed during this phase's available validation.

Final validation: affected build has zero warnings/errors. Shipping: 18 passed, 0 failed, 6 skipped. All available suites: 221 passed, 0 failed, 64 skipped after targeted reruns of obsolete Order-boundary and Wallet-permission-migration assertions. Total skips are 60 SQL tests, one broker test and three true Payment end-to-end scenarios blocked by Phase 8.
