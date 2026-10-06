# Refund Processing — Phase 11

Refund coordination owns `refund.Refunds` and its migration history in a small Onion module (Domain, Contracts, Application, Infrastructure). This is a separate scheduling/consistency responsibility: Wallet remains the sole owner of balances and financial ledger entries. No public refund-issuing endpoint, Payment implementation, gateway, Shipping, Redis or infrastructure installation is added.

## Current dependency limitation

Phase 8 Payment is absent. `IRefundPaymentEvidence` is a read-only **future integration contract**, not a Payment implementation. No production adapter or fake verification is registered. Refund requests can be recorded durably from the existing Inventory failure contract, but the scheduler leaves them Pending without exactly one authoritative adapter. Completion also refuses credit without that adapter. The true Payment -> Inventory -> refund -> Wallet end-to-end test is explicitly blocked.

Phase 8 must implement evidence backed by successful, server-verified payment records, preserving payment state and validating PaymentId, OrderId, owner, actual amount and currency. The adapter must return evidence only for a verified successful payment eligible for this full Inventory refund, with no competing refund or reversal. No client claims or the payment event's ActorId establish the refund recipient. This phase supports one full refund per PaymentId; partial, split/multiple-source payment allocation and competing refund workflows are outside its scope.

## Durable flow and transaction ownership

1. Phase 10's Inventory payment-success consumer attempts existing stock deduction. On shortage it rolls all deductions back and persists its existing `inventory.unavailable.v1` Outbox event with a Rejected Inbox record. It does not credit Wallet. Payment state is not modified.
2. `refund.inventory-unavailable.v1` consumes that existing failure event through the shared Inbox. A narrow Order read contract supplies immutable owner/payable amount/currency. These define a candidate full refund and do not establish proof of successful payment. A stable PaymentId-derived RefundId/credit IdempotencyKey and database unique indexes prevent duplicate requests, including new message IDs for the same payment. Reused PaymentId with a changed OrderId is rejected.
3. Refund, `refunds.requested.v1` Outbox and consumed Inbox commit atomically through the existing local SQL transaction participant. Original schedule, amounts, correlation and completion survive replay.
4. Due timestamps are stored in SQL. A periodically polling worker loads due Pending IDs, acquires a transaction-owned per-payment application lock, reloads state, sets Processing and increments AttemptCount. It persists `refunds.due.v1` with a stable ID per refund attempt in the same transaction as the state change. Competing schedulers cannot dispatch the same attempt twice. Processing means a durable attempt has been scheduled, not that Wallet was credited.
5. The existing confirmed Outbox publisher delivers the event through RabbitMQ. The Refund due consumer joins the shared Inbox transaction and Refund participant, validates due time/state/attempt and acquires the same payment lock. Obsolete retry attempts are ignored.
6. Authoritative payment evidence must exactly match all stored identifiers, owner and money. Mismatch becomes a durable Failed refund with a safe failure event; unavailable evidence remains retryable. Missing adapter is an explicit dependency failure and never authorizes Wallet credit.
7. Existing `IWalletOperations.CreditAsync` credits the stored owner, with the same owner recorded as the beneficiary actor, stable credit key, `InventoryRefund` business reference to PaymentId and a fixed safe description. Wallet uses its existing independent atomic balance/ledger transaction. No Wallet entities/tables are accessed by Refund production code.
8. Completed Refund, `refunds.completed.v1` Outbox and Inbox commit together. Broker acknowledgement follows durable Inbox commit. If Wallet commits but completion/Inbox/Outbox persistence fails, the same Wallet key and unchanged posting fingerprint recover the original ledger result on retry. This is an idempotent consistency protocol, not a distributed/exactly-once transaction.

All integration events use the existing transactional Outbox. There is no application/domain direct broker publication, duplicate Inbox implementation, one-hour sleep or `Task.Delay`. Source IDs/money/reason/schedule and completed records cannot be edited/deleted through EF. The migration adds only owned objects and refuses destructive downgrade.

## Configuration and broker routes

`RefundProcessing` settings:

| Setting | Default | Meaning |
| --- | --- | --- |
| Enabled | false | Registers the durable scheduler worker when true |
| DelayMinutes | 60 | Delay from first durable refund creation; saved in DueAtUtc |
| PollingSeconds | 15 | Poll interval, not the financial delay |
| BatchSize | 20 | Maximum due IDs examined per batch |
| MaximumAttempts | 8 | Bounded persisted evidence/Wallet attempts |
| RetryMinutes | 5 | Exponential retry base, capped at 1 day |

Existing requests keep their stored delay when configuration changes. Zero delay is supported explicitly; negative or over-30-day delay is rejected. Workers and Messaging default to disabled. Enabling scheduling does not bypass the missing evidence adapter.

Reuse Phase 10 deployment settings and secrets. Configure separate consumer queues with these existing `Messaging:RabbitMq:Queues` consumer names/routes:

| ConsumerName | RoutingKeys |
| --- | --- |
| inventory.payment-succeeded.v1 | payments.succeeded.v1 |
| refund.inventory-unavailable.v1 | inventory.unavailable.v1 |
| refund.due.v1 | refunds.due.v1 |

Each requires its own durable queue and dead-letter queue. Bind an appropriate durable downstream/audit queue for `refunds.requested.v1`, `refunds.completed.v1` and `refunds.failed.v1`; mandatory publication will retain/retry an unroutable event. Broker credentials/topology remain external configuration. Restrict event publication to trusted services; no HTTP payload can authorize or specify a refund amount/recipient. Shared SQL participants require the same physical database and `MultipleActiveResultSets=False` for Inbox savepoint rollback.

## Failure/recovery and audit

Wallet/evidence failures store safe FailureCode, AttemptCount and NextAttemptAtUtc. Refund is never marked Completed when credit fails. Transient failure returns to Pending and a new attempt gets a new delivery ID while retaining the original credit key. At the bounded limit, Failed retains the request and emits `refunds.failed.v1`; nothing is silently deleted. A lost Wallet response can represent an already committed credit: retry uses the stable key to resolve it, and terminal cases require reconciliation against Wallet's ledger before action.

Crash before scheduling commit leaves Pending; after commit leaves Processing plus durable Outbox. A completion persistence failure leaves Processing and the original due delivery for existing Inbox retries. Exhausted Inbox/Outbox failures stay visible in their terminal records. Operators must reconcile failed/Processing refunds with Wallet's posted `InventoryRefund` reference before controlled recovery, preserving RefundId, PaymentId, original money, owner and IdempotencyKey. This phase adds no unsafe automatic reset, financial-history purge or arbitrary administration endpoint.

Refund identity, creation/due/completion timestamps, safe failure code/count, correlation and WalletTransactionId provide durable coordination audit. Wallet retains before/after balance and immutable ledger audit. No sensitive payment data, payloads, credentials or raw exception messages are logged by Refund code.

## Validation and future infrastructure tests

Available tests use explicit InMemory transaction/store substitutes and test-only payment evidence; Wallet posting tests reuse real application/domain services and existing Wallet test doubles. Stock shortage tests reuse existing Inventory commands/test doubles. They demonstrate behavior and replay safety without claiming SQL locking/atomicity or live broker delivery.

Five SQL tests are implemented and skipped unless `REFUND_TEST_SQL_SERVER` is supplied for a future explicitly authorized run: Refund/Outbox rollback, concurrent requests and unique keys, failure Inbox/request atomicity, concurrent due scheduling and duplicate credit delivery, and Wallet commit followed by Refund rollback/Inbox retry. The fixture prohibits LocalDB and touches only its checked GUID-named disposable test database. It does not run on this machine.

The existing Messaging RabbitMQ integration test now includes duplicate persistent `refunds.due.v1` messages with preserved IDs; it remains skipped without `MESSAGING_TEST_RABBITMQ`. True Payment end-to-end coverage is separately blocked by absent Phase 8 and is not replaced with a fake Payment module.

`InitialRefund` and `artifacts/refund-migration.sql` are generated/reviewed offline. No migration is applied, persistent local database accessed/modified or SQL Server/LocalDB/Docker/RabbitMQ service installed or started.

Final validation: affected build succeeded with zero warnings/errors. Refund tests: 20 passed, 0 failed, 6 skipped. All available suites: 203 passed, 0 failed, 58 skipped, including the successfully rerun Messaging suite after adding its Refund contract project reference. Total skips are 55 SQL tests, one broker test and two true Payment end-to-end tests blocked by Phase 8.
