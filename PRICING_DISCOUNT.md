# Pricing and Discount — Phase 6

Pricing owns authoritative variant prices. Discount owns promotion rules. Each module has Domain, Contracts, Application, Infrastructure and Presentation projects, a SQL Server context, its own schema and migration history. Foundation response/error handling, configuration, correlation, logging and Identity permissions are reused.

## Calculation rules

* All monetary values use decimal; SQL storage uses decimal(18,4). Base and compare prices must be positive and at most 1,000,000,000,000. Compare price cannot be below base price and does not determine discount savings.
* Supported currencies are IRR, USD, EUR, GBP, AED and TRY. This implementation uses whole IRR units and two decimal places for the other currencies. Inputs must already respect this precision. Discount savings round per unit, midpoint away from zero, before quantity multiplication. Currency conversion is outside this phase.
* Price identity is a stable record ID with an immutable variant/currency pair. Prices can be scheduled. Periods use inclusive start and exclusive end; a null price end is unbounded. Inactive records may overlap, active records may not. Current queries capture server time rather than accepting a client clock.
* Percentage values must be positive, at most 100, with at most four decimal places. Fixed discounts are per unit in the explicit rule currency, positive and within the money bounds. Savings clamp to the base price, so final prices cannot be negative.
* Exactly one rule applies per line: highest priority wins, then largest rounded unit saving, then smallest rule GUID using .NET GUID comparison. Discounts do not stack. Applicable-discount queries also list other eligible rules; this list does not imply stacking.
* Rules require active state, matching currency and effective period, remaining usage capacity, and matching coupon when configured. Codes normalize to uppercase ASCII letters/digits/underscore/hyphen, up to 64 characters. Multiple rules can share a code and use the same selection rule.
* A rule targets either one variant, one product, one exact category, or all products. Category targeting does not include descendants. Management commands validate references through Catalog contracts.
* MinimumOrderAmount is checked against the server-calculated sum of base price × requested quantity for all supplied lines, before discounts. A single-variant preview uses that line's subtotal; a cart preview uses its current lines. No client subtotal is accepted.
* Preview requests contain only variant ID, requested quantity, currency and optional coupon. Quantity is 1–999, batches have at most 100 distinct variants. Missing/inactive Catalog variants are rejected; missing current prices return 404.

## API

All routes require authentication. Actors and cart ownership come from the authenticated sub claim. Management additionally requires pricing.manage or discount.manage, added to the existing Administrator role through an incremental Identity migration.

| Route | Operation | Permission |
| --- | --- | --- |
| POST /api/v1/pricing/prices | Create price, 201 with Location | pricing.manage |
| GET /api/v1/pricing/prices/{id} | Get management record | pricing.manage |
| PUT /api/v1/pricing/prices/{id} | Replace price details | pricing.manage |
| PATCH /api/v1/pricing/prices/{id}/status | Activate/deactivate | pricing.manage |
| GET /api/v1/pricing/variants/{variantId}?currency=IRR | Current price | Authenticated |
| GET /api/v1/pricing/variants?variantIds={id}&currency=IRR | Batch current prices; repeat variantIds | Authenticated |
| GET /api/v1/pricing/variants/{variantId}/history?currency=IRR&page=1&pageSize=20 | Paged audit snapshots | pricing.manage |
| POST /api/v1/pricing/preview | Fresh single-line quote | Authenticated |
| GET /api/v1/pricing/variants/{variantId}/discounts?currency=IRR&quantity=1&couponCode=SALE | Eligible rules | Authenticated |
| GET /api/v1/cart/pricing?currency=IRR&couponCode=SALE | Fresh owner cart preview | Authenticated |
| POST /api/v1/discounts | Create discount, 201 with Location | discount.manage |
| GET /api/v1/discounts/{id} | Get rule | discount.manage |
| GET /api/v1/discounts?page=1&pageSize=20 | List rules | discount.manage |
| PUT /api/v1/discounts/{id} | Replace rule details | discount.manage |
| PATCH /api/v1/discounts/{id}/status | Activate/deactivate | discount.manage |

Responses use ApiResponse and errors use Foundation Problem Details with correlation IDs. Invalid input returns 400, missing records/references 404, and overlapping/stale writes 409. Batch current-price reads return the records found; calculation requires every requested variant to have a current price.

Example preview body:

```json
{ "productVariantId": "<variant-guid>", "quantity": 2, "currency": "IRR", "couponCode": "SALE" }
```

Each quote line includes base/compare unit prices, selected discount ID, unit saving, total saving for the requested quantity, final unit price and total line amount. At base 1,000,000, a 20% rule yields 800,000 per unit; a fixed 150,000 rule yields 850,000.

## Persistence, concurrency and audit

Pricing transactions acquire a transaction-owned SQL application lock scoped to variant/currency before loading mutable price state and checking overlap. This protects different start dates and activation races without broad Serializable isolation. SQL additionally enforces uniqueness for active records with identical variant/currency/start. Rowversion protects stale writes. Price changes and append-only history snapshots commit atomically; snapshots include actor, action, UTC time and correlation. EF rejects history updates/deletes and its price relationship restricts deletion.

Discount commands use transactions and rowversion. Rules retain last-changing actor and UTC time; a complete historical Discount audit log is outside this phase. UsageLimit and UsedCount are persisted and checked for eligibility. Preview never consumes usage. There is no redemption API or accounting flow: atomic usage consumption must accompany a future accepted checkout/order workflow.

No cross-module database foreign keys or table access exist. Catalog's small variant-reference contract now provides product/category IDs and a production batch projection to avoid N+1 reads. It owns no price. Cart continues to own requested quantities only and has no Pricing, Discount or Inventory dependency. Its separate preview endpoint is hosted by Pricing Presentation and uses Cart.Contracts. Preview does not persist prices, reserve/deduct stock, create movements, or establish checkout payable amounts.

Quotes are informational snapshots, not binding offers or final order totals. Price and discount reads occur in their own contexts and can change between requests. A later checkout must recalculate and implement its own acceptance, redemption and audit boundaries. No Checkout, Order, Payment, Wallet, refunds, messaging, caching or campaign engine was added.

## Migrations and validation

InitialPricing, InitialDiscount and AddPricingDiscountPermissions were generated offline. Reviewed forward scripts are in artifacts/pricing-migration.sql, artifacts/discount-migration.sql and artifacts/pricing-discount-permissions.sql. The permission script assumes prior Identity migrations are already applied. No migration was applied during Phase 6.

The PricingDiscount test suite covers domain validation, commands/queries/history, calculation/rounding/eligibility/targets, HTTP permission policies, client-money rejection, owner-only fresh cart pricing, SQL model constraints and SQL read-projection translation. Database-free persistence tests use an explicit EF InMemory provider and substitute transaction units; they do not establish SQL locking, rollback or rowversion behavior. Offline SQL projection tests block connection opening.

SQL-backed tests are implemented for persistence/history, overlapping concurrent creates, activation/deactivation serialization, rollback/stale rowversion, and Cart/Inventory/usage isolation. They skip when PRICING_TEST_SQL_SERVER is absent. A future authorized run requires an externally provided disposable SQL Server and permission to create/drop a uniquely named MyOnlineShop_PricingTests_* database. LocalDB is rejected. The fixture never targets an existing application database. No SQL Server, LocalDB or Docker was installed or launched for this phase.
