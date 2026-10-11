# Payment and Order orchestration

Phase 8 supplies Payment records, provider contracts, initiation/verification commands, DTO APIs, authoritative refund evidence and purchase orchestration. **No real gateway adapter or credentials are configured.** Production uses an unavailable provider and returns 503 before creating/binding a payment. Simulated providers exist only in tests. No live gateway, SQL Server, Redis, RabbitMQ or Docker validation was performed.

## Provider and configuration

A real adapter must implement `IPaymentProvider` and be explicitly registered. It must honor the server-generated PaymentId as the gateway initiation idempotency/merchant reference, recover an accepted initiation after timeout, and verify by authenticated server-to-server retrieval. Its result must identify the exact merchant payment, provider reference, authoritative amount/currency and unique receipt. A browser redirect, callback body or client status is never proof.

`PaymentGateway:Endpoint`, `MerchantId` and `Credential` are optional secure adapter configuration. Supply them through environment variables (`PaymentGateway__*`) or an appropriate secret provider, never source files. If supplied, all three must be present and the endpoint must use HTTPS without URL user information. Configuration alone does not install or activate an adapter. A real adapter must also implement its gateway-specific callback authenticity, merchant/account association, allowed redirect/callback URLs, currency support and final-status semantics; no generic HTTP protocol was guessed here. Credential rotation, real settlement/reversal semantics and gateway validation remain release blockers.

`Payment:ProviderTimeoutSeconds` defaults to 30 (allowed 1–120). Provider I/O occurs outside SQL transactions, with cancellation and bounded waits. An accepted gateway operation followed by local failure is retried with the original PaymentId, requiring gateway idempotency; no automatic second charge is issued under another ID.

For physical orders, set `Payment__FulfillmentWarehouseId` (Compose: `PAYMENT_FULFILLMENT_WAREHOUSE_ID`) to an explicitly selected existing warehouse. Missing selection fails closed. Existing Inventory read APIs validate active/available stock there; availability is not reserved. This phase does not guess warehouse priorities, nearest-warehouse rules, stock splitting or fallback selection. The unresolved business decision is which warehouse an operator should select, or whether a later trusted per-order allocation policy is required. A physical Order must have a captured Shipping selection before payment initiation; historical orders lacking it need an intentional fresh checkout. Digital-only orders allocate no physical stock.

## API and state

* `POST /api/v1/payments`: authenticated owner, OrderId and IdempotencyKey; 201 with a DTO and Location.
* `GET /api/v1/payments/{id}`: owner-only DTO, 404 for another owner.
* `POST /api/v1/payments/{id}/verify`: authenticated owner; provider reference requests server verification.
* `POST /api/v1/payments/{id}/callback`: anonymous gateway notification; bounded provider reference only, server verification required, 204 without owner/payment details.

Responses use existing Foundation envelopes/errors and no-store headers. Input cannot set amount, currency, owner, success, receipt, allocation or fulfillment. Provider exceptions are replaced with safe errors and no credentials, references, bodies or receipts are logged.

Payment status is Created → Initiated → Succeeded or Failed. Pending provider verification leaves Initiated unchanged. A Succeeded payment is reverified against the provider; only matching authoritative success with the original receipt is a duplicate, and it emits no second event. Pending/Failed/Reversed/Chargeback, changed receipts or malformed server results place it on a durable reconciliation hold. A failed payment cannot be silently promoted by a conflicting delayed result. No client status establishes a reversal or chargeback. A hold blocks new initiation, verification, Inventory fulfillment and new refund credits. No public endpoint clears it or fabricates success. An operator must reconcile original IDs and gateway evidence. Fulfillment tracks Pending → Fulfilled, or Pending → RefundPending → Refunded. One Payment per Order and owner/key uniqueness prevent competing local attempts; multiple attempts, partial/split payments, wallet-funded purchases and zero-payable/free-order completion remain outside scope.

Order binding prevents customer/admin cancellation or failure while a bound payment is unresolved. Explicit provider failure rejects the bound Order. Provider outages retain Created/Initiated state for same-key recovery; an accepted initiation whose local response was lost can also recover from authenticated server verification of the original PaymentId, reference, amount and currency. No speculative timeout cancellation or expiry is introduced.

## Successful fulfillment transaction

1. Checkout keeps the existing historical amount/currency/items/shipping snapshot and AwaitingPayment state.
2. Initiation binds a unique Payment to that Order, persisting immutable server-owned allocation inputs before gateway I/O.
3. Server verification checks merchant PaymentId, stored provider reference, exact amount/currency and receipt.
4. Payment Succeeded and the existing `PaymentSucceededIntegrationEventV1` commit with Messaging in one local SQL transaction.
5. Existing Outbox/RabbitMQ delivers the event to the existing Inventory consumer.
6. The production consumer requires Payment orchestration. It locks/validates the persisted successful Payment and exact allocation/event identity; untrusted/mismatched events cannot deduct stock.
7. Inbox enlists Inventory, Payment and Order. Existing conditional stock UPDATE/movements, Payment Fulfilled, Order Paid/audit and Inbox completion commit together. Any failure rolls them back; no Order is confirmed before deduction.
8. Existing Inbox/event fingerprints and stable Payment/warehouse/variant operation IDs protect duplicate delivery. Digital orders use the same verified flow with no physical deductions.

Shared transactions require the same physical SQL database and MARS disabled. Internal orchestration uses module-owned contracts/transaction participants, not another module's repositories/tables. This remains a modular monolith.

## Stock shortage, Refund and Wallet

The existing consumer rolls back all deductions and emits the existing InventoryUnavailable event with rejected Inbox state. The Refund consumer enlists Refund, Payment and Order: it claims exclusive refund eligibility, marks the unfulfilled Order Failed and persists the existing durable Refund/Outbox request atomically. A fulfilled Payment cannot claim that refund and a refund-claimed Payment cannot fulfill.

The existing 60-minute due time/scheduler/retries remain unchanged. Payment evidence reads persisted server-verified records only after refund eligibility is claimed and includes the hold state. Refund-due Inbox enlists Payment and holds its transaction-owned order lock through evidence checking, Wallet posting/readback and completion persistence, excluding concurrent hold changes from authorizing a new credit. Refund checks Order/payment/owner/amount/currency and uses the original stable Wallet key/reference. Wallet retains its independent atomic ledger/balance transaction. If a credit already committed before Refund completion failed, a hold permits read-only recovery of that owner's matching posted credit; no second credit request is issued. A held payment with no posted credit cannot create a new credit. Mismatched posted money/reference/actor fails completion.

Bind `payment.refund-completed.v1` to `refunds.completed.v1` for Payment's completion consumer (added to local Compose). It records Refunded; Order remains Failed and cannot initiate Shipping. Fulfilled Orders can use the existing paid-physical-Order shipment API. No shipment is created automatically, and existing Shipping transitions do not automatically advance Order.

Initiation rejects a known primary-Wallet currency mismatch before creating/binding a new Payment or calling the provider; recoverable Created attempts recheck before provider I/O. Lookup failure prevents charging. With no existing Wallet, no currency or refund destination is invented. A Wallet created concurrently or while an already-issued gateway operation is in flight can still introduce a mismatch; an approved currency-reservation/refund policy is needed to eliminate that race. No FX or alternate destination is selected.

Terminal message/refund failures still require reconciliation using original IDs and Wallet ledger. Reversed/Chargeback evidence sets a hold without automatically reversing stock, crediting a Wallet or inventing settlement rules. A hold cannot undo a gateway call already in flight or stop an already-dispatched shipment; previously confirmed/shipping Orders require operational review. Automatic Shipping hold enforcement for previously fulfilled Orders is not added by this focused remediation. Accepted-purchase discount usage accounting, external reversal settlement, partial refunds and general financial reconciliation remain outside scope.

## Persistence and validation

Payment owns the `payment` schema/history: Payments and Allocations, decimal(18,4), rowversion, bounded amounts/quantities/statuses, unique Order, owner/initiation key, provider reference and provider receipt. Order gains nullable PaymentId without cross-module EF foreign keys or historical backfill.

Generated offline migrations:

* `20261010102105_InitialPayment` and Payment model snapshot.
* `20261010102143_BindOrderPayment` and updated Order model snapshot.
* `20261011053228_AddPaymentReconciliation` and updated Payment model snapshot.
* `20261011071018_AddPaymentReconciliationReason` adds only nullable bounded Payment-owned diagnostic classification; no provider payload is stored.

Original idempotent forward SQL was generated/reviewed in `artifacts/phase8-payment.sql` and `artifacts/phase8-order-payment.sql`. The remediation's incremental script is `artifacts/payment-safety-reconciliation.sql`. All are additive, with no historical rewriting or destructive forward operations. The original Payment/Order checks had no pending changes; the current Payment check also passes. Downgrade refuses deleting financial/reconciliation/binding history. Apply Order binding and all Payment migrations intentionally only after approval and before enabling consumers; none was applied here. See [DOCKER.md](DOCKER.md) for owner-context tooling and [MESSAGING.md](MESSAGING.md) for worker settings.

Database-free tests exercise real commands, JWT/API infrastructure, domain transitions and production consumer orchestration with explicitly simulated gateway/store/deduction dependencies. These are not real gateway or SQL E2E evidence. Four opt-in `PAYMENT_TEST_SQL_SERVER` tests compile but skip: concurrent callbacks, Payment/Outbox rollback, Inventory race/durable Refund/Wallet deduplication, and Inbox rollback of deduction/Payment/Order. Their disposable GUID database fixture rejects LocalDB and can create/migrate/drop only its generated database when separately authorized. Its gateway is simulated and its seeded Orders are unpaid; no Payment is seeded as successful. Existing legacy messaging/refund SQL suites isolate their infrastructure with explicit contract/evidence doubles; they do not establish verified Payment E2E.

Original Phase 8 validation: API build passed with zero warnings/errors; Payment **31 passed, 0 failed, 4 skipped**. Nine affected suites totaled **206 passed, 0 failed, 58 skipped** (54 SQL, one RabbitMQ, three real Payment E2E placeholders), in `artifacts/phase8-tests`. The remediation results below supersede the affected-suite counts, without claiming a fresh full regression. No real gateway or external-service integration passed because none was run.

## Changed files

New Payment files under `src/Modules/Payment`:

* `Contracts/PaymentContracts.cs`, `Contracts/MyOnlineShop.Payment.Contracts.csproj`.
* `Domain/Payment.cs`, `Domain/MyOnlineShop.Payment.Domain.csproj`.
* `Application/PaymentApplication.cs`, `Application/PaymentOrchestration.cs`, `Application/MyOnlineShop.Payment.Application.csproj`.
* `Infrastructure/DependencyInjection.cs`, `Infrastructure/PaymentProvider.cs`, `Infrastructure/MyOnlineShop.Payment.Infrastructure.csproj`, `Infrastructure/Persistence/PaymentDbContext.cs`, `Infrastructure/Persistence/PaymentPersistence.cs`.
* `Infrastructure/Persistence/Migrations/20261010102105_InitialPayment.cs` and `.Designer.cs`, `20261011053228_AddPaymentReconciliation.cs` and `.Designer.cs`, `PaymentDbContextModelSnapshot.cs`.
* `Presentation/DependencyInjection.cs`, `Presentation/PaymentController.cs`, `Presentation/MyOnlineShop.Payment.Presentation.csproj`.

Purchase integration and host files:

* `MyOnlineShop.slnx`, `MyOnlineShop/MyOnlineShop.csproj`, `MyOnlineShop/Program.cs`.
* `src/Modules/Order/Contracts/OrderPaymentWorkflow.cs` (new), `Domain/Order.cs`, `Infrastructure/DependencyInjection.cs`, `Infrastructure/Persistence/OrderPaymentWorkflow.cs` (new), `Infrastructure/Persistence/Migrations/20261010102143_BindOrderPayment.cs` and `.Designer.cs` (new), `Infrastructure/Persistence/Migrations/OrderDbContextModelSnapshot.cs` (Order-relative paths).
* `src/Modules/Inventory/Contracts/IInventoryPaymentOrchestration.cs` (new), `Application/PaymentSucceededConsumer.cs`, `Infrastructure/DependencyInjection.cs` (Inventory-relative paths).
* `src/Modules/Refund/Contracts/IRefundPaymentPreparation.cs` (new), `Application/RefundConsumers.cs`, `Infrastructure/DependencyInjection.cs` (Refund-relative paths). Wallet, Shipping and shared Messaging production implementations are reused without changes.

Tests and documentation/configuration:

* New `tests/MyOnlineShop.Payment.Tests/{MyOnlineShop.Payment.Tests.csproj,PaymentTests.cs,PaymentApiAndModelTests.cs,SqlTests.cs}`.
* `tests/MyOnlineShop.Messaging.Tests/{SqlTests.cs,MessagingTests.cs}`, `tests/MyOnlineShop.Refund.Tests/{SqlTests.cs,RefundTests.cs}`, `tests/MyOnlineShop.Shipping.Tests/SqlTests.cs`: explicit legacy infrastructure doubles and honest E2E skip reasons.
* `scripts/Test-Regression.ps1`, `compose.yaml`, `.env.example`.
* `PAYMENT.md` (new), `README.md`, `DOCKER.md`, `MESSAGING.md`, `REFUND.md`, `ORDER.md`, `HARDENING.md`, `PROJECT_STATUS.md.txt`.

Generated ignored validation artifacts: the two forward SQL scripts and `artifacts/phase8-tests/*.trx`/`summary.json`.

## Focused payment safety remediation

Provider-reference/receipt unique-key errors retain the original database exception in safe application-error context. Only SQL error number and allowlisted index identity are classified in memory; provider values, payloads and SQL diagnostic text are never logged or persisted. After the failed transaction rolls back/detaches, a separate bounded unit persists ReconciliationRequired and a first safe ReconciliationReason. Failure of that follow-up write returns explicit `payment_reconciliation_recording_failed`, retaining both error contexts internally; it does not claim the hold was stored. Complete database unavailability or a crash between rollback and recording still requires external settlement reconciliation.

The default production provider remains unavailable. Real adapter verification, reversal semantics and external charge idempotency remain unverified. SQL validation is still required for concurrent success/conflicting callbacks, actual uniqueness-error classification and rollback-followed-by-hold persistence, recording failure recovery, concurrent hold/refund-credit exclusion and wallet-commit/held-refund recovery. RabbitMQ redelivery/recovery and true gateway-to-Shipping E2E also remain unverified. Database-free failure injection checks application sequencing, not SQL transaction guarantees.

Final remediation validation: affected API build **0 warnings/errors**; Payment **50 passed/4 skipped**, Refund **24/6**, Wallet **22/6**, Foundation **17/1**: **113 passed, 0 failed, 17 skipped** (16 SQL and one real Refund E2E placeholder). Reports/summary: `artifacts/payment-safety-tests`. Payment has no pending model changes; the incremental forward SQL adds only nullable `ReconciliationReason`. No services contacted or migrations applied.

Remediation changed files (module-relative paths below):

* `src/Modules/Payment`: `Application/PaymentApplication.cs`, `Application/PaymentOrchestration.cs`, `Application/MyOnlineShop.Payment.Application.csproj`, `Domain/Payment.cs`, `Infrastructure/Persistence/PaymentDbContext.cs`, `Infrastructure/Persistence/PaymentPersistence.cs`, new `Infrastructure/Persistence/PaymentPersistenceConflictClassifier.cs`, new `Infrastructure/Persistence/Migrations/20261011071018_AddPaymentReconciliationReason.cs`/`.Designer.cs`, `Infrastructure/Persistence/Migrations/PaymentDbContextModelSnapshot.cs`.
* `src/Modules/Wallet`: `Contracts/WalletContracts.cs`, `Infrastructure/Persistence/WalletReadStore.cs`, `Infrastructure/DependencyInjection.cs` (read-only safety port; posting/ledger unchanged).
* `src/Modules/Refund`: `Contracts/RefundContracts.cs`, `Domain/Refund.cs`, `Application/RefundConsumers.cs`, `Application/RefundProcessing.cs`, `Infrastructure/DependencyInjection.cs`.
* `tests/MyOnlineShop.Payment.Tests`: new `PaymentSafetyTests.cs`, `PaymentTests.cs`, `PaymentApiAndModelTests.cs`, `SqlTests.cs` (constructor support; SQL cases remain skipped).
* `tests/MyOnlineShop.Refund.Tests`: `RefundTests.cs`, `TestSupport.cs`; new `tests/MyOnlineShop.Wallet.Tests/PaymentSafetyReadTests.cs`.
* `PAYMENT.md`, `PROJECT_STATUS.md.txt`; ignored forward SQL and test-report artifacts above. Inventory, Outbox/Inbox and Shipping business implementations are unchanged.

True gateway → broker → Inventory/Refund/Shipping E2E cases remain explicitly skipped. SQL locks, transaction enlistment, constraints, rowversions, broker confirms/recovery and live refund recovery are unverified. Production readiness is not claimed.

