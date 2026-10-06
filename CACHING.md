# Phase 13 — Redis & Caching

Redis is optional. SQL Server remains authoritative. Cache integration lives in BuildingBlocks Infrastructure behind `ICacheService`; modules cache DTOs at their read-store boundary. Domain, Application, controllers and business contracts have no Redis client dependency. No migrations or business schema changes are required.

## Configuration

`MyOnlineShop/appsettings.json` disables Redis by default and contains no connection credentials. Enable explicitly using configuration or environment variables:

```text
Redis__Enabled=true
Redis__ConnectionString=<securely supplied Redis connection configuration>
Redis__InstanceName=MyOnlineShop:development:
Redis__DefaultExpirationMinutes=2
Redis__OperationTimeoutMilliseconds=500
Redis__FailureCooldownSeconds=15
Redis__MaximumPayloadBytes=262144
```

Use a different InstanceName per environment/application. Supply authentication and TLS settings through your secret configuration provider, never committed settings. Disabled mode does not open a Redis connection. Enabled mode connects lazily and falls back when Redis is unavailable. Existing SQL Server/JWT configuration requirements are unchanged.

The implementation uses Microsoft's [.NET 10 distributed Redis cache package](https://www.nuget.org/packages/Microsoft.Extensions.Caching.StackExchangeRedis/10.0.0), pinned to the repository's existing framework package baseline, and its StackExchange.Redis client. Connect retry is zero, the backlog fails fast, and each asynchronous operation has a bounded deadline. Failure starts a 15-second process-local cooldown; retries resume on later requests. Warnings contain operation/error type only, never exception messages, cache values, keys, credentials, tokens or personal information.

## Cached reads

| Read | Key resource | Invalidation |
| --- | --- | --- |
| Catalog product details, including variants/specifications/images/metadata | `product:{id}:public` or `:management` | Every successfully committed Catalog command |
| Catalog category and brand details | `category:{id}:public/management`, `brand:{id}:public/management` | Every successfully committed Catalog command |
| Pricing individual price record DTO | `price:{id}:management` | Every successfully committed Pricing command |
| Discount individual rule DTO | `rule:{id}:management` | Every successfully committed Discount command |
| Active Shipping methods | `methods:{normalizedCurrency}:active` | Successfully committed method creation/update |

Permissions and ownership checks remain at their existing boundaries. Catalog public and management representations are distinct. Price/discount lookup DTOs are reachable only through existing protected endpoints; there is no role-name bypass or cached authorization result. No user-specific data, shipment addresses, cart state, authentication data or financial state is cached.

List/search queries, current-price single/batch reads, discount eligibility candidates, final pricing calculations, Catalog purchasability references, Inventory availability and final Shipping quotes always query authoritative stores. Checkout and Order totals therefore do not depend on Redis freshness. Identity, Cart, Inventory, Order, Payment, Wallet and Refund business flows are unchanged.

## Keys, expiration and invalidation

Full keys are `{InstanceName}v1:{module}:{generation}:{resource}`. `v1` is the serialization/read-model schema version; bump it for incompatible DTO changes. Generation metadata uses `{InstanceName}v1:{module}:generation` and contains a random token with a 24-hour absolute TTL. Data has a two-minute absolute TTL by default, validated between one and ten minutes, and a 256 KiB payload limit. Oversized DTOs bypass storage. JSON uses System.Text.Json web defaults. Missing/null data is a miss; null results and business exceptions are not cached. Corrupt JSON is warned about, removed, and reloaded.

After a successful transaction commit, the module changes only its generation token. All affected product/taxonomy relationships and old in-flight reads become unreachable together; other modules remain intact. Old entries expire naturally. There is no key scan, FLUSHDB, database wipe, distributed lock or public cache endpoint. Invalidation uses a bounded, non-request-cancelled operation so request cancellation after SQL commit does not suppress it. Failed commands never invalidate. Shipment state/tracking commands do not invalidate method configuration.

Redis invalidation is best effort, not a distributed SQL/Redis transaction. If a process stops after SQL commit or Redis fails during invalidation, existing display/management DTOs can remain stale until their absolute TTL expires. Other instances may see that bounded staleness during outages. Direct SQL edits bypass application invalidation and have the same TTL bound. Financial calculations and eligibility checks stay correct because they bypass these caches.

64 fixed process-local semaphore stripes coalesce concurrent misses without accumulating an unbounded dictionary of locks. This protects each instance; multiple instances may independently reload the same DTO. Reads make a bounded number of Redis operations independent of variant count, and serialize only the DTO once. No distributed coordination is introduced.

## Validation

Normal tests use a fake distributed cache, controlled time and explicitly configured EF InMemory contexts. They require no server and do not prove SQL transaction behavior. Coverage includes versioned keys, DTO serialization, hit/miss, expiration, module invalidation, in-flight stale fills, stampede protection, failure cooldown/recovery, timeout/cancellation, malformed/oversized payloads, disabled startup, Catalog visibility, commit-hook control flow and authoritative pricing/discount/shipping bypass.

`RedisIntegrationTests` compiles but skips unless `REDIS_TEST_CONNECTION_STRING` is explicitly supplied later. It checks DTO round-trip, expiry and removal using a unique GUID namespace and expiring keys; it never flushes Redis. The Phase 13 validation clears all SQL Server, RabbitMQ and Redis integration connection variables before running the available suite. No servers are installed, started or contacted. Live Redis and SQL invalidation runtime verification remain deferred.
