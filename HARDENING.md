# Phase 16 — Testing & Hardening

Phase 16 stabilizes the existing modular monolith. It does not supply the missing Phase 8 Payment module or claim production readiness. Database-free tests provide evidence for application rules, API policies and infrastructure control flow; SQL transactions, row locking, uniqueness enforcement, broker confirms and Redis runtime behavior require separate authorized integration runs.

## Regression strategy

Run `powershell -NoProfile -File scripts/Test-Regression.ps1` from the repository. The runner builds and tests the thirteen relevant test projects serially, starting with Foundation/Identity. It clears only process-level infrastructure-test connection variables and disables inherited external-service/bootstrap features, then restores the original process settings in `finally`. No server is installed or started, no migration is applied, and no persistent database is contacted. TRX reports and a per-module `summary.json` are written to `artifacts/phase16-tests` by default. The runner stops on a failing project and preserves its TRX diagnostics. It does not build the whole solution.

`-SummarizeOnly` regenerates the summary from existing TRX files without executing tests. Skip counts use individual NotExecuted outcomes: the xUnit TRX aggregate notExecuted counter can incorrectly be zero. An outcome-count consistency check prevents trusting incomplete summaries.

| Area | Available regression evidence | Deferred integration evidence |
| --- | --- | --- |
| Foundation | Production error/log safety, malformed JSON, validation errors, correlation, Swagger visibility | SQL connectivity |
| Identity | Hashing/password rules, real JWT signature/issuer/audience/expiry/claims, refresh rotation/replay/logout, persisted roles/permissions, activation, over-posting and denied admin APIs | Concurrent registration/refresh rotation, SQL uniqueness and transactional audit |
| Catalog | Generic attributes/SKUs/hierarchy, public versus management visibility, validation, pagination, safe administration audit | Existing model/migration checks run offline; broader live query plans remain deferred |
| Inventory | Receipt/adjustment rules, idempotency, signed movements, warehouse activation/policies, adjustment-history DTOs and offline SQL translation | Stock=1/two buyers, duplicate concurrent deduction, atomic rollback and SQL constraints |
| Cart | Owner isolation, quantities, no reservation, current pricing reads | SQL persistence/concurrency |
| Pricing/Discount | Decimal/rounding/currency rules, eligibility/coupons, overlap validation, server recalculation and separate permissions | SQL schedule locking, constraints and transactions |
| Order | Server-priced immutable snapshots, checkout/cancellation idempotency, ownership, status transitions, management date/status/paging | Order/Cart/Outbox commit/rollback and concurrent checkout |
| Payment | Versioned integration contract and existing Inventory consumer only | Phase 8 producer, verification, amount verification and paid-Order integration are absent |
| Wallet | Decimal amounts, ledger invariants, idempotency/fingerprint conflicts, business references, ownership and narrowly authorized admin credit | Concurrent debit/credit, conditional balance updates, ledger/rollback integrity and database immutability |
| Outbox/Inbox/messaging | Durable retry/dead state policies, stable redelivery, completion-failure replay, poison handling, lease-loss/cancellation behavior, acknowledgement ordering and safe failure logs | Enlisted SQL Outbox/Inbox transactions, exclusive leases/savepoints, duplicate concurrent delivery and real RabbitMQ confirms |
| Refund | Server snapshot candidate amounts, delayed eligibility, retry/completion state, stable Wallet credit key, mismatched evidence rejection and no credit without evidence | SQL rollback/concurrency and true Payment-to-refund E2E; production evidence adapter is absent |
| Shipping | Owner isolation, server costs, tracking/revisions, valid progression and rejected Delivered/Cancelled regressions, admin filters | SQL rowversion/conflicts and verified-payment fulfillment |
| Redis/cache | DTO-only cache, absolute TTL, module invalidation, in-flight stale-fill isolation, failure/timeout/cancellation fallback and unchanged authoritative calculations | Real Redis expiry/connection behavior |
| Reporting/Admin | Owner read ports, decimal/currency-separated aggregates, offline SQL translation, bounded report dates, scoped policies, DTO privacy, pagination and duplicate-route checks | Live SQL aggregate execution/plans and missing verified-payment reports |

Existing infrastructure tests use explicit opt-in SQL/RabbitMQ/Redis attributes/connection variables and remain compiled but skipped in this runner. Missing-Phase-8 E2E tests remain explicitly blocked, rather than being replaced by fake Payment business behavior. Before any future live run, review the test fixture: several SQL suites create/migrate/delete an isolated generated test database and must only be enabled against a separately authorized disposable endpoint.

## Changes made after review

* Message envelopes now validate correlation identifiers as at most 128 ASCII letters/digits or `-`, `_`, `.`; the existing empty value remains supported when correlation is unavailable. Invalid control characters cannot enter message logs. Existing HTTP correlation IDs satisfy this constraint. Legacy malformed durable messages are retained as Dead; historical rows may need operator review before future publishing if they used other formats.
* Outbox dispatch now checks the reconstructed envelope against its persisted fingerprint and the configured UTF-8 payload limit before publishing. Corrupted/oversized rows are retained as Dead with safe error text. The fingerprint detects payload inconsistency; it is not a producer authentication mechanism. No event is marked Published before the publisher succeeds. Existing completion-failure retries remain at-least-once with Inbox/business idempotency.
* Outbox failure logs include only validated event/correlation metadata, preventing invalid stored values from being echoed. Processor failures that occur outside the Inbox handler now produce a safe diagnostic with message ID, consumer and failure category before requeue; no exception object, broker credential, SQL connection string or payload is logged. Shutdown cancellation leaves delivery unacknowledged/claimed for existing recovery mechanisms.
* Focused real-JWT tests exercise existing authentication and persisted authorization across admin modules, reject duplicate mandatory claims, reject malformed/unsigned tokens and prove token role/permission claims cannot override stored assignments. No authentication bypass was added. Production exception tests capture both formatted log entries and exception objects to verify safe behavior for generic/database errors.

These are bounded changes to existing abstractions and dispatchers. No business module, transaction pattern, public business endpoint, permission/grant, package upgrade, database model, index or migration was added. A test compilation error involving an init-only options property was corrected before successful validation.

## Security and correctness review

| Check | Finding and evidence |
| --- | --- |
| Authentication/authorization | Real JWT tests plus existing rotation/replay/revocation, activation and immediate permission-change tests. Admin policies require persisted explicit permissions; role names cannot bypass management policies. |
| User isolation | Existing Cart, Order, Wallet and Shipping owner-scoped queries/API tests return safe denials for another user; client actor/owner fields cannot replace authenticated identity. |
| Input and over-posting | DTO validation bounds quantities, money/currency precision, collection sizes, strings, IDs, dates, statuses and pagination. Existing malformed JSON/oversized password/invalid money tests run. Client totals, balances, actor, stock quantity, payment/refund state are not writable through DTOs. |
| Financial integrity | MoneyRules uses decimal and currency precision; Wallet conditional SQL uses explicit decimal parameters. Ledger/idempotency/business-reference uniqueness and reconciliation checks are retained. Refund candidate amounts come from historical Order snapshots; credit requires matching authoritative payment evidence. |
| Inventory concurrency | Reviewed existing atomic conditional UPDATE with parameterized delta/warehouse/variant, nonnegative/maximum bounds and returned affected-row result. Transaction ownership, operation locks, unique stock/operation keys and rowversions are preserved. The existing stock=1 concurrency test compiles but cannot be claimed as executed without SQL Server. |
| Transactions/idempotency | Reviewed Inventory, Order, Wallet and Refund units of work; Order enlists Cart/Messaging; Refund enlists Messaging; Inbox owns participant transactions/savepoints. Refund-to-Wallet uses a separate existing Wallet transaction and stable credit key to recover commit/completion gaps. Outbox/Inbox uniqueness remains database-backed. Existing SQL rollback tests are deferred. |
| State machines | Order cannot be manually marked Paid or skip transitions. Shipment terminal regressions and stale revisions fail. Refund completion/duplicate due events preserve the same credit/state. Payment state machine is unavailable. |
| Secrets/errors/logging | No configured signing secret is added; JWT validation avoids error-detail/token retention. New and existing tests check password/token and exception-detail safety. Metadata admin logs depend on a configured durable logging sink, as documented in Phase 15. |
| SQL injection/database model | Reviewed EF LINQ/interpolated SQL and explicit parameters on critical quantity, balance and lock statements. Existing model/script tests verify unique keys, restrictive relationships, decimal precision, rowversions and constraints offline. No dynamic client SQL was introduced. |
| Cache authority | Catalog display DTOs may be stale within TTL; final pricing, eligibility, Inventory and financial state stay uncached/SQL-authoritative. Cache tests cover failure, invalidation and cancellation. No private user cache is introduced. |
| Performance | Existing list DTO projections/AsNoTracking/stable paging and owner-side report aggregates are retained. No new per-item queries or blanket indexes/optimizations. Live execution plans/load tests are still required. |

## Validation results

Initial and final affected API builds passed with zero warnings/errors. The serialized thirteen-project regression passed **304 tests, failed 0 and skipped 67**. A final focused Messaging hardening rerun passed 10 cases after removing timing sensitivity from the cancellation fixture; these are included in the 304 unique cases. Skips comprise 62 SQL-dependent tests, 1 RabbitMQ test, 1 Redis test and 3 true Payment E2E cases blocked by Phase 8. Corrected TRX summaries are available in `artifacts/phase16-tests/summary.json`; focused results are in `artifacts/phase16-focused/MessagingHardening.trx`.

No infrastructure was installed, started or contacted and no database was modified. PowerShell execution is subject to the machine's existing policy; this work did not change that policy.

## Remaining release blockers and limits

Phase 8 Payment remains unimplemented. Verified payment production/amount verification, the paid-Order producer path, true Payment E2E validation, verified-payment reports and authoritative production refund evidence are pending. Phase 16 does not change that status.

SQL concurrency and atomic rollback tests have not run here; InMemory/test doubles cannot establish oversell prevention, database transaction behavior or rowversion correctness. Broker publisher-confirm/connection recovery and real Redis behavior are likewise unverified. These are release blockers for relying on the corresponding production flows, not successful integration validation.

Metadata audit retention still depends on a configured logging sink and has a possible commit-to-log crash gap. Retry-exhausted Dead messages need an operational review/replay process; this phase adds no administrative replay workflow. Reporting reads remain independent snapshots, rather than a single cross-module transaction. External-service configuration, TLS/host/reverse-proxy policy, deployment secrets, load profiling and operational monitoring must be validated in the intended deployment environment.

The next recommended work is the separately authorized Phase 8 Payment implementation, followed by authorized isolated infrastructure integration validation. No later phase is started here.
