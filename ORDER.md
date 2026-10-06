# Order and Checkout — Phase 7

Order has Domain, Contracts, Application, Infrastructure and Presentation projects. It owns the ordering SQL Server schema, context and migration history. Existing Foundation responses/errors, configuration, correlation/logging and Identity authentication/permission policies are reused.

## Checkout

POST /api/v1/checkout accepts an IdempotencyKey GUID, explicit Currency, optional CouponCode and optional Address snapshot. It accepts no user ID, item prices, discount amounts, totals or availability claims. Ownership always comes from the authenticated sub claim.

1. Normalize and validate the request and calculate its fingerprint.
2. Start the local Order transaction, enlist Cart, and acquire a transaction-owned SQL application lock scoped to the authenticated user.
3. Resolve an existing user/key result before inspecting or clearing the current Cart.
4. Read the owner's active Cart and its revision; reject empty, duplicate or invalid items.
5. Batch-read Catalog variant references with SKU, product name, kind and purchasability.
6. Use the existing centralized Pricing calculation for current prices, discounts and currency rounding. Verify that returned IDs/quantities match the Cart.
7. Snapshot server-supplied unit prices, savings and line totals. Sum subtotal, total discount and payable amount from the lines.
8. Read Inventory's batch availability contract for physical variants. Only active stocks in active warehouses count; quantities are aggregated across warehouses. This does not allocate fulfilment to a warehouse. Digital variants do not require warehouse stock.
9. Create the Order and its immutable items/address, transition Pending to AwaitingPayment and append the checkout audit.
10. Clear only the exact Cart ID/revision inspected. Save Order/items/audit and Cart clearing in the same SQL transaction and commit.

Inventory availability is advisory, not a promise of future fulfilment. Checkout never calls Inventory commands/deduction, reserves stock, creates movements, or holds inventory locks through payment. Two checkouts may both observe enough stock. Final atomic deduction remains a later verified-payment responsibility.

Address is optional in this phase. When supplied, recipient, street, city, postal code and two-letter country code are validated and retained historically. No profile-address or Shipping implementation was added. Payable amount is the sum of discounted lines; tax, shipping charges, payment gateways and wallet/refund behavior are outside this phase. Fully discounted orders still await the future payment workflow.

## Money and snapshots

Every amount uses decimal with decimal(18,4) persistence. The existing Pricing policy applies: whole IRR units, two decimal places for USD/EUR/GBP/AED/TRY, midpoint-away-from-zero savings rounding per unit before quantity multiplication. Checkout does not introduce additional intermediate rounding or currency conversion.

The existing maximum money amount of 1,000,000,000,000 also bounds order subtotal and line totals. Unsupported precision, negative/inconsistent savings, invalid quantities and excessive totals are rejected before Cart clearing.

OrderItem snapshots retain variant ID, SKU, product name/kind, PriceId, selected DiscountId, base unit price, unit/total discount, final unit price, quantity and line total. Root totals, currency, pricing timestamp and optional address are retained. Historical reads query Order data only; they do not reconstruct purchases from current Catalog/Pricing/Discount state.

Snapshot properties have no public mutation methods. EF rejects edits/deletes to item/address/audit snapshots, root purchase fields and Order deletion. Only status, updated time and revision may change. Restrictive owned-module relationships preserve history. No cross-module foreign keys or direct internal entity/table dependencies were introduced.

Unpaid checkout checks current discount eligibility but does not redeem or consume discount usage. Atomic purchase/redemption accounting remains for the verified-payment workflow. Creating AwaitingPayment orders does not guarantee later discount capacity or stock availability.

## Lifecycle

| Current state | Allowed next states |
| --- | --- |
| Pending | AwaitingPayment, Cancelled, Failed |
| AwaitingPayment | Paid, Cancelled, Failed |
| Paid | Processing |
| Processing | Shipped |
| Shipped | Completed |
| Completed, Cancelled, Failed | None |

Checkout persists AwaitingPayment. Customers may cancel only their own Pending/AwaitingPayment orders. Cancellation of paid or terminal orders is rejected and performs no inventory/payment/refund action. Administrative status changes follow the same graph; requests to set Paid are always rejected. Paid is a domain state reserved for the later trusted verification workflow. Processing/Shipped/Completed transitions are status bookkeeping, with no Shipping integration.

Creation and status changes append actor/action/UTC-time/correlation audit records in the same Order transaction. Root rowversion rejects concurrent stale status writes.

## Idempotency and transaction boundaries

Idempotency information is persisted on Order with a unique user/key index. The fingerprint hashes structured, normalized currency/coupon/address data. Reusing the same key for a different request returns 409. A successful retry returns the same Order ID and stored purchase snapshot, with its current lifecycle state, even if the Cart, Catalog or prices have changed. It never clears a newly populated Cart. Use a new key for a new purchase. POST returns the original resource's 201/Location on retries.

A unique Cart ID/revision index additionally prevents duplicate consumption of one snapshot. A narrow user checkout lock serializes competing checkout requests, while existing Cart revision and rowversion checks detect concurrent Cart mutations. Validation failure or a changed Cart rolls back checkout. A transient retry after an ambiguous commit resolves the persistent key before doing more work.

ILocalSqlTransactionParticipant is an infrastructure-only enlistment abstraction in BuildingBlocks. Cart implements it and the separate ICheckoutCart business contract; Order Infrastructure coordinates its own context and Cart's context through one SQL connection/native transaction. There is no ambient/distributed transaction or access to Cart internals from Order. Pricing, Catalog and Inventory are read through contracts in their own contexts and are not enlisted.

This atomic boundary relies on the current shared physical SQL Server database and scoped module contexts. If Order/Cart are extracted into separate services/databases, replace local enlistment with a durable checkout-consumption protocol and compensating/reconciliation workflow. No messaging or extraction implementation was added now.

## API

All endpoints require authentication and return existing ApiResponse/Problem Details with correlation IDs.

| Endpoint | Behavior | Permission |
| --- | --- | --- |
| POST /api/v1/checkout | Create/replay checkout; 201 and Order Location | Authenticated |
| GET /api/v1/orders?page=1&pageSize=20 | Own paged summaries | Authenticated |
| GET /api/v1/orders/{id} | Own historical details | Authenticated |
| POST /api/v1/orders/{id}/cancel | Cancel own eligible order | Authenticated |
| GET /api/v1/orders/management | Paged administrative summaries | orders.view |
| GET /api/v1/orders/management/{id} | Administrative details | orders.view |
| PATCH /api/v1/orders/management/{id}/status | Allowed status change | orders.manage |

Unknown/other-owner customer resources return 404. Invalid input/state returns 400, invalid authentication 401, missing management permission 403, and stale writes, insufficient availability or changed idempotency payloads 409. Page size is 1–100.

## Persistence and validation

InitialOrder adds ordering tables, relationships, money/quantity/status constraints, rowversion and unique idempotency/cart-revision indexes. AddOrderPermissions adds orders.view/orders.manage and Administrator grants incrementally; old migrations remain unchanged.

Reviewed offline forward scripts: artifacts/order-migration.sql and artifacts/order-permissions.sql. The permission script assumes the preceding Identity migrations are applied. No migration was applied and no application database was created/reset/modified.

Database-free tests use actual commands/calculation/read projections and the HTTP pipeline with explicit InMemory contexts and substitute transaction units. They cover totals, discounts/rounding, snapshots, validation, ownership, cancellation/state rules, persisted replay behavior, availability and the mandatory stock=5/quantity=2/no-movement case. These substitutes do not prove SQL atomicity or concurrency. Offline SQL model/read-projection checks block connection opening.

Five SQL-backed tests are implemented and skipped while SQL Server is unavailable. They cover persisted checkout/no stock changes, concurrent same-key requests, rollback after Cart and Order flushes through the shared connection, changed-Cart detection and SQL uniqueness/ownership/status audit/rowversion. A future authorized run uses ORDER_TEST_SQL_SERVER and requires permission to create/drop a uniquely named disposable MyOnlineShop_OrderTests_* database. The fixture rejects LocalDB and never targets an existing application database. No SQL Server, LocalDB or Docker was installed or launched.
