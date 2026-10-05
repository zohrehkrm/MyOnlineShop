# Cart — Phase 5

Cart owns the user's requested SKU quantities. Its Domain, Contracts, Application, Infrastructure and Presentation projects follow the existing module structure. It reuses Foundation responses/errors/configuration/logging and Identity authentication. Catalog's existing `ICatalogVariantReferences` supplies existence and purchasability; no Catalog or Identity redesign was needed.

## Behavior and boundaries

A user has at most one persisted active cart. GET is read-only: when no cart exists, it returns an empty DTO with a null Id/timestamps. The first AddCartItem command creates the active cart. Adding the same variant increments its existing item quantity and preserves the item ID. Updating replaces that quantity. Removing an item returns 404 if it does not belong to the user's active cart. Clear is safe for an empty or nonexistent cart and keeps an existing cart active.

Limits are 1–999 whole units per item and 100 distinct items per cart. These bounds protect request/cart size, not stock availability. Add and update validate that Catalog considers the variant active/purchasable, including Catalog-owned product/category/brand/attribute rules. Both physical and digital variants are supported. Existing items remain visible if a variant later becomes inactive; quantity changes are rejected, but removal/clear remain available. Later checkout must revalidate the current Catalog state.

Cart has no dependency on Inventory or Pricing. It does not read, change, reserve or lock stock, create inventory movements, or reduce available inventory. Requested quantity may exceed stock. Responses contain IDs, quantities and UTC timestamps; no prices, discounts, product names supplied by clients, or totals are persisted/calculated. Final price and stock checks belong to later phases.

UserId and ProductVariantId are logical module references. Cart's database does not have cross-schema foreign keys to Identity or Catalog. The authenticated server context supplies UserId; Catalog's contract validates variant references. No Cart endpoint accepts another user's ID as authority.

## API

All routes require the existing authenticated Identity context. The `sub` claim supplies the owner. No additional management permission or Identity migration is needed.

| Method | Route | Result |
|---|---|---|
| GET | `/api/v1/cart` | 200, current or empty cart |
| POST | `/api/v1/cart/items` | 200, cart after adding/incrementing |
| PUT | `/api/v1/cart/items/{id}` | 200, cart after quantity replacement |
| DELETE | `/api/v1/cart/items/{id}` | 204, item removed |
| DELETE | `/api/v1/cart` | 204, cart cleared |

Add accepts only ProductVariantId and Quantity. Update accepts only Quantity. Unknown client fields such as UserId, Price, Total or ProductName confer no authority and are not used. Responses use Foundation's `ApiResponse<T>`; errors use Problem Details with correlation. Invalid input/purchasability returns 400, unauthenticated/invalid subject returns 401, missing or foreign items return 404, and concurrent write/uniqueness conflicts return 409.

POST adds a quantity, rather than setting an absolute quantity. PUT sets an absolute quantity. Clients should reload after a conflict before retrying their intended change.

## Persistence and concurrency

`CartDbContext` owns schema `cart` and its own migration history, using externally configured SQL Server and Foundation retry/timeout options. Commands use a tracked aggregate repository and transaction. Queries use an untracked DTO projection.

The filtered unique UserId index enforces one active cart; the CartId/ProductVariantId unique index prevents duplicate items. A check constraint enforces quantity bounds. CartItem belongs to Cart with aggregate-owned cascade deletion, including item removal/clear. Application-generated GUIDs are explicitly configured as not database-generated.

Root rowversion protects overlapping writes. Every child mutation changes the cart Revision so EF updates/checks the root even when only items changed. A concurrency failure rolls back the transaction and returns 409. Concurrent first-cart creation conflicts also return 409 through the unique constraint. No application lock or Serializable transaction is added. This detects overlapping server writes; it does not implement client ETags for stale edits submitted after another request has finished.

`InitialCart` adds only Cart tables/indexes/relationships. The reviewed idempotent forward script is `artifacts/cart-migration.sql`. No migration was applied and no database was created/reset. There is no automatic startup migration.

## Validation

```powershell
dotnet build tests/MyOnlineShop.Cart.Tests/MyOnlineShop.Cart.Tests.csproj
dotnet test tests/MyOnlineShop.Cart.Tests/MyOnlineShop.Cart.Tests.csproj --no-build --no-restore
```

Affected build: 0 warnings/errors. Cart tests: 13 passed, 5 SQL-backed tests skipped. Available tests cover empty read/first creation, duplicate add, update/remove/clear, validation/limits, inactive variants, user isolation, API authentication/server-owned user identity/correlation, Inventory independence, and offline SQL Server model/migration/query translation.

Command/API tests explicitly substitute EF InMemory and a test unit of work. They do not prove SQL constraints, transactions or rowversion concurrency. The offline SQL query test blocks connection opening. The stock=1 test confirms Cart quantity changes leave Inventory and movements unchanged, including a requested quantity above stock; Cart production projects have no Inventory/Pricing references.

Five SQL tests are implemented for later Docker SQL Server: persisted CRUD/owner isolation; unchanged Inventory/no new movement; two updates loaded at the same revision yielding one success and one 409; active-cart/item unique constraints; and rollback on a combined-quantity limit failure. They compiled but could not run without SQL Server.

For later authorized Docker verification, set `CART_TEST_SQL_SERVER` to a disposable container's master connection and run this test project. Its fixture creates/migrates/cleans up only `MyOnlineShop_CartTests_<guid>`, rejects LocalDB and never resets an existing application database. Inventory migrations and fixture stock are used only inside that disposable test database to verify Cart isolation. The variable was cleared for current test processes.

Pricing, discounts, coupons, checkout, orders, payment, refunds, stock reservation and Phase 6 remain unimplemented.
