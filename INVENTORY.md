# Inventory — Phase 4

Inventory follows the existing Domain, Contracts, Application, Infrastructure and Presentation layout. It owns Warehouse, Stock, InventoryMovement, StockReceipt and StockAdjustment. Commands use a focused persistence store and unit of work; queries use untracked, paginated EF projections. DTOs are returned by the API.

## Model and boundaries

Stock is unique per warehouse/Catalog variant. Quantities are whole SKU units (`long`), bounded from zero to 1,000,000,000,000. A packaged or weighted product is counted in its SKU units; fractional bulk measurements are not supported in this phase. Current quantity is physical on-hand quantity. Available quantity equals on-hand quantity when stock and warehouse are active, otherwise zero. Catalog sellability remains a separate concern.

Catalog's small `ICatalogVariantReferences` contract supplies variant identity, product kind and activation. Only Catalog accesses its own product/variant tables. Inventory stores an opaque ProductVariantId and validates it through that contract; there is no cross-module database foreign key. Inventory does not duplicate SKU or product rules. Physical warehouses reject digital products. Receipts/returns can accept inactive or Draft physical variants; sale deduction requires an active Catalog reference.

Warehouse and stock can be deactivated. Stock settings include a low-stock threshold. Movements reference immutable stock identity, which resolves their warehouse and variant. Movement history contains signed quantity, before/after balances, type, operation ID, business reference, UTC timestamp, authenticated actor, reason and correlation ID. Receipt/adjustment records reference their corresponding movement. EF protects all three history entities against modification/deletion; relationships restrict deletion.

## Atomic changes and retries

Quantity changes use a parameterized SQL `UPDATE` with warehouse/variant and active-state predicates, a nonnegative resulting quantity and an upper bound. `OUTPUT` returns the actual before/after quantities. No matching row means the operation fails with 409 and no movement is created. There is no unprotected SELECT-then-UPDATE deduction.

The SQL update, movement and receipt/adjustment record share one transaction. A failure rolls them all back. Database check constraints additionally prohibit negative stock and inconsistent movement balances. SQL Server locks the changed stock row; two different deductions of the last unit cannot both satisfy the conditional update. Transactions use the provider default isolation, not broad Serializable isolation.

Every quantity operation requires a stable OperationId GUID and a business Reference. Reuse the same OperationId when retrying the same operation. A transaction-owned SQL application lock serializes retries of that ID, and a unique movement index prevents duplicate persistence. An exact replay returns the original movement; a changed warehouse, variant, delta, type, reference, reason or actor returns 409. Correlation ID is not part of the fingerprint, allowing a retry under a new request correlation ID. Failed operations leave no successful movement and can be retried with the same ID. A separate narrow application lock protects creation of the first stock row during positive operations.

The Reference can be shared across line items; each line's operation ID must remain unique and stable. Generating a new operation ID for every retry defeats idempotency. Stock updates and movement records retain the execution strategy/retry behavior configured by Foundation.

## API and permissions

Routes begin with `/api/v1/inventory`. Existing Identity permission claims/policies are reused. `AddInventoryPermissions` seeds four permissions and Administrator grants through an incremental migration. Existing role permission administration can grant/revoke them.

| Operation | Route | Permission |
|---|---|---|
| List/get warehouses | GET `warehouses`, `warehouses/{id}` | `inventory.view` |
| Create/update warehouse | POST `warehouses`, PUT `warehouses/{id}` | `inventory.adjust` |
| Get stock by warehouse/variant | GET `warehouses/{warehouseId}/variants/{variantId}` | `inventory.view` |
| Stock list, warehouse inventory, variant inventory, low stock | GET `stocks` | `inventory.view` |
| Configure stock active state/threshold | PUT `stocks/{stockId}/settings` | `inventory.adjust` |
| Receive purchased stock | POST `receipts` | `inventory.receive` |
| Return stock | POST `returns` | `inventory.receive` |
| Manual adjustment or damage | POST `adjustments` | `inventory.adjust` |
| Movement/receipt history | GET `movements`, `receipts` | `inventory.view` |

List parameters include `page`, `pageSize` (1–100), `warehouseId` and `productVariantId`. Stock lists also support `isActive` and `lowStockOnly`. Warehouse lists support `isActive`. History supports `movementType=Receipt|Sale|Return|Damage|ManualAdjustment`. Ordering includes ID tie-breakers. Adjustments supply signed QuantityDelta and Type; Damage requires a negative delta. Receipt and return inputs supply positive Quantity.

Create warehouse returns 201 with Location; updates, quantity operations and queries return 200. Quantity operations use 200 for both first execution and idempotent replay. Foundation provides success envelopes, Problem Details errors, correlation and structured logging. API actor IDs come from the authenticated `sub` claim, never request input. Validation returns 400, missing resources 404, state/quantity/idempotency conflicts 409 and authentication/permission failures 401/403.

Deduction is exposed only through the trusted application contract `IInventoryDeduction`, with no HTTP deduction endpoint. `inventory.deduct` is registered for future authorized internal use. A later checkout/payment handler must invoke deduction only after successful payment verification and supply a trusted actor plus stable operation ID. This phase implements neither that workflow nor Payment/Order. Cart reservation, refund processing, pricing, messaging and Redis are not implemented.

## Persistence and validation

`InventoryDbContext` owns schema `inventory` and its migration history, reusing `ConnectionStrings:SqlServer` and Foundation database options. `InitialInventory` creates only Inventory tables, keys, constraints and indexes. `AddInventoryPermissions` changes only Identity permission/grant seeds. Reviewed forward scripts are `artifacts/inventory-migration.sql` and `artifacts/inventory-permission-migration.sql`; neither was applied. There is no startup migration or database creation.

```powershell
dotnet build tests/MyOnlineShop.Inventory.Tests/MyOnlineShop.Inventory.Tests.csproj
dotnet test tests/MyOnlineShop.Inventory.Tests/MyOnlineShop.Inventory.Tests.csproj --no-build --no-restore
```

Available validation: 15 Inventory tests passed; 8 SQL-backed tests skipped. Unit/API tests use explicit persistence and Catalog test doubles and do not prove database atomicity/concurrency. Offline tests validate SQL Server model/migrations, full paged read SQL translation and reference lookup translation, with database connections blocked. Focused Catalog contract/migration regressions also passed.

The SQL suite implements receipt consistency and rollback, adjustment/return history, successful/exact/insufficient deduction, movement failure rollback, concurrent last-unit deductions, concurrent duplicate operation retries, concurrent first receipts and append-only history. Runtime SQL locking, constraints and rollback remain pending Docker SQL Server verification.

For later authorized integration testing, set `INVENTORY_TEST_SQL_SERVER` to the disposable Docker SQL Server endpoint's master connection, then run the Inventory test project. The fixture creates, migrates and cleans up only its uniquely named `MyOnlineShop_InventoryTests_<guid>` database. It rejects LocalDB and never resets an existing application database. The variable was cleared for current test processes, so no fixture connected to SQL Server during Phase 4 validation.
