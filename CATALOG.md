# Catalog — Phase 3

Catalog owns product descriptions and SKU identity. Its five projects follow the existing Domain, Contracts, Application, Infrastructure and Presentation layout. Commands use aggregate repositories and a unit of work; queries use untracked EF projections through a separate read store. DTOs are the module boundary.

## Model and rules

Categories have an optional parent, a unique normalized code and an active flag. Cycles, missing parents and active descendants under inactive ancestors are rejected. Brands have unique codes and an active flag. Management uses deactivation rather than hard deletion.

Reusable attributes contain named values. Product attribute options select the values available to that product; each variant selects exactly one allowed value from every configured attribute. For example, reusable Color and Size attributes can describe a shirt, while Color, Storage and RAM describe a phone. Product has no clothing-specific fields. Specifications are independent name/value/unit entries, not SKU dimensions. Metadata is a bounded key/value collection.

Variant IDs are stable GUIDs. SKU is normalized to uppercase ASCII and unique across Catalog. A hash of sorted attribute/value IDs makes combinations unique within a product, regardless of input ordering. Products without configurable attributes have one base SKU. Once variants exist, their attribute dimensions cannot change, and options in use cannot be removed; unused options can change. Variant selections and SKU can be updated while preserving the variant ID.

Products start as Draft. Add variants before setting Status to Active. An active product requires an active variant and active category/brand references. Public reads hide Draft/Archived products, inactive category/brand references and variants with inactive attributes/values. Management reads include inactive resources. Physical products and variants can have weight in grams and dimensions in millimeters; digital products cannot carry physical measurements.

Images are HTTPS URLs; Catalog does not upload or fetch them. Sort orders are unique per product. At most one image can be explicitly primary; otherwise the lowest sort order becomes primary. PUT product replaces attribute options, images, specifications and metadata, while preserving variants. Send the complete intended product representation.

## API and authorization

Routes use `/api/v1/catalog`. Public GET collection/detail routes exist for `categories`, `brands`, `attributes` and `products`. Management GET routes use `manage/{resource}` and `manage/{resource}/{id}`. POST collection and PUT detail routes require `catalog.manage`, as do attribute value and product variant operations:

- `POST attributes/{id}/values`, `PUT attributes/{id}/values/{valueId}`
- `POST products/{id}/variants`, `PUT products/{id}/variants/{variantId}`

All routes return existing `ApiResponse<T>` envelopes or Foundation Problem Details errors. Create returns 201 with a Location, update/get/list return 200, invalid input returns 400, missing resources return 404, conflicts return 409, and management authentication/permission failures return 401/403.

Lists support `page`, `pageSize` (1–100) and `search`. Products support `categoryId`, `brandId`, `status` and `sort=name|newest`. Taxonomy lists support `isActive`; categories also support `parentId`. Product search includes name, description and SKU. Ordering has an ID tie-breaker for stable paging.

The existing Identity permission mechanism is reused. `AddCatalogPermission` adds only the `catalog.manage` permission and Administrator grant. Existing role permission administration can grant/revoke it; there is no role-name bypass.

## Persistence and migrations

`CatalogDbContext` owns the `catalog` SQL Server schema and its own migration history. It reuses the externally configured `ConnectionStrings:SqlServer`, Foundation command timeout and retry options. No startup migration or database creation runs automatically.

`InitialCatalog` adds Catalog tables, indexes, check constraints and relationships. Composite foreign keys enforce attribute ownership and product/variant option membership. Taxonomy and SKU relationships restrict deletion; product detail rows and unused options use aggregate-owned cascade deletion. Unique indexes protect codes, SKUs, combinations, image ordering/primary selection, specification names and metadata keys.

Each command is a transaction. Rowversion on aggregate roots detects concurrent writes; product and attribute revisions ensure child mutations update the root. Category changes also hold a transaction-owned SQL application lock to prevent concurrent hierarchy cycles. Concurrent uniqueness and reference failures map to safe API errors. This protects overlapping server writes; the API does not implement client ETag/precondition semantics.

Offline-reviewed forward scripts are `artifacts/catalog-migration.sql` and `artifacts/catalog-permission-migration.sql`. The latter is incremental from InitialIdentity. Neither script was applied. For later Docker SQL Server verification, apply the existing Foundation/Identity migrations and the new Identity and Catalog migrations to the authorized disposable container database.

## Validation

```powershell
dotnet build tests/MyOnlineShop.Catalog.Tests/MyOnlineShop.Catalog.Tests.csproj
dotnet test tests/MyOnlineShop.Catalog.Tests/MyOnlineShop.Catalog.Tests.csproj --no-build --no-restore
```

26 focused tests cover domain rules, command/query behavior, persisted aggregate changes, API status/validation/correlation handling, authorization policies and offline SQL Server model/migration generation. Command/API tests explicitly substitute EF InMemory and a test transaction implementation; they do not validate SQL translation, SQL transactions, rowversion behavior, SQL locks or relational constraint execution. Those database-dependent integration checks are skipped/deferred until Docker SQL Server is available. Production persistence remains SQL Server only.

Pricing, quantities, reservations, inventory and later phases are outside Catalog. No RabbitMQ or Redis dependency was introduced.
