# Transactional Outbox and RabbitMQ — Phase 10

Shared contracts live in BuildingBlocks.Abstractions.Messaging. BuildingBlocks.Infrastructure.Messaging owns MessagingDbContext, the `messaging` SQL schema and its migration history. Business modules depend on IOutboxWriter/IIntegrationEvent/IMessageConsumer, not RabbitMQ types. Foundation registers persistence; the API separately registers the publisher and optional hosted workers.

## Producer atomicity

Checkout enqueues OrderCreatedIntegrationEventV1 (`orders.created`, version 1) while creating the historical Order and clearing its Cart. OrderUnitOfWork enlists the Messaging context through the existing infrastructure-only ILocalSqlTransactionParticipant mechanism, sharing the Order connection/transaction with Cart and Messaging. The strict production writer refuses writes without an enlisted SQL transaction. It flushes the Outbox row within that transaction; Order/Cart/Outbox commit or roll back together. Checkout replays return the existing order and create no additional event. No business service calls RabbitMQ.

New producers must enlist Messaging before calling IOutboxWriter, supply a stable EventId and safe module-owned contract, and commit it with their business changes. Do not enqueue after commit. The generic LocalSqlTransactionParticipant helper supports other module contexts without exposing SQL objects in application contracts. This mechanism requires the shared physical SQL Server database; it is not a distributed transaction or a microservice consistency protocol.

## Outbox and publishing

Outbox stores event identity/type/version, JSON payload, fingerprint, UTC created/processed/attempt/due timestamps, correlation, attempt counter, safe error, status, lease ID/expiry and rowversion. Event identity is the primary key; changed content under an existing ID is rejected. Source envelopes and Inbox deduplication identities are protected against rewriting/deletion through EF. InitialMessaging refuses destructive downgrade of durable message/history tables. No automatic purge or administrative recovery endpoint is introduced.

States: Pending → Publishing → Published; retryable failures return to Pending with persisted bounded exponential backoff; exhausted/permanent failures become Dead and retain their payload and diagnosis. RetryCount counts claimed delivery attempts, including the first attempt. SQL claims use one atomic UPDATE/OUTPUT with locking hints compatible with READ_COMMITTED_SNAPSHOT. Expired leases are recoverable after crashes. Each message's lease is renewed before sending; status updates are fenced by the current lease ID so an obsolete worker cannot complete/fail another worker's claim.

OutboxWorker uses BackgroundService and configurable PeriodicTimer polling. RabbitMQ outages affect delivery, not already committed business state. The RabbitMQ.Client adapter uses serialized access to its publisher channel, persistent messages, durable exchanges/quorum queues, mandatory routing and awaited publisher confirmations. Unroutable publishes, nacks and connection failures do not mark a message Published. Connections are lazy and are recreated after failures. Cancellation leaves claimed work recoverable through lease expiry.

Delivery is **at least once**. Publish may succeed before a crash or failed completion update; the same stable event ID and envelope can then be sent again. Neither broker confirms nor this design promise exactly-once delivery. Dead records require deliberate operational review/recovery; they are never silently discarded.

## Inbox and consumer transactions

Inbox primary key `(MessageId, ConsumerName)` deduplicates each consumer independently. It retains the immutable envelope/fingerprint, state, processing time, attempt count, safe error and persisted retry eligibility. An exclusive transaction-owned SQL application lock serializes the same consumer/message across processes. Changed envelopes under the same identity are rejected; successful/rejected duplicates acknowledge without reprocessing.

SqlMessageProcessor owns one local SQL transaction, enlists the consumer's module contexts and creates a savepoint before handler work. Existing InventoryUnitOfWork joins that transaction without opening/committing a nested one. Successful stock changes/movements and the processed Inbox row commit together. Handler errors roll back business work and durably record bounded retry/dead state. Invalid contracts are permanent failures. Infrastructure failure before durable state can be recorded leaves the broker delivery unacknowledged/requeued. SQL Server savepoint support is required; use connections with MultipleActiveResultSets disabled.

ConsumerDeliveryDispatcher acknowledges only after durable processing returns Processed, Duplicate or Rejected. Retryable/deferred messages are negatively acknowledged with requeue, with a short configurable throttle; retry counts/due times live in SQL, not in that timer. Dead/invalid messages are rejected without requeue and routed to the configured dead-letter queue. Valid dead messages also retain their Inbox envelope for diagnosis/recovery. Malformed envelopes that cannot establish a valid identity rely on broker quarantine. Queue/exchange durability and broker policies/availability must be verified in the deferred RabbitMQ tests.

Workers propagate incoming correlation into the existing request-context contract for module audit, log IDs/types/consumer/attempt/correlation and safe failure categories, and suppress repeated polling connection warnings until recovery. They never log payloads, exception messages, connection strings, credentials, tokens or payment secrets.

## Inventory contract and missing Payment producer

PaymentSucceededIntegrationEventV1 (`payments.succeeded`, version 1) is an **inbound Inventory contract**, not a Payment implementation. It contains EventId, UTC occurrence time, PaymentId, OrderId, server actor, decimal amount/currency and explicit warehouse/variant/whole-quantity lines. No credentials, card data, tokens or profile information are included. Digital-only payments may have no stock lines. The future verified producer must validate order/payment amounts and authorize/allocate those lines before emitting this contract. Broker credentials and ACLs must restrict publishing to trusted application producers; there is no public event-ingestion endpoint.

Inventory's consumer calls the existing Phase 4 IInventoryDeduction service, preserving its conditional SQL quantity change and signed movement history. Per-line operation IDs derive deterministically from PaymentId/warehouse/variant, protecting repeated business instructions even if another delivery ID is used. Lines are processed in a stable order and all participate in the Inbox transaction. No stock reservation is introduced.

If stock is unavailable, the processor rolls back **all** deductions, records a Rejected Inbox entry and enqueues InventoryUnavailableIntegrationEventV1 (`inventory.unavailable`, version 1) in the same transaction. Its distinct stable event ID cannot collide with the incoming source event. This is a failure boundary for later work, not refund execution or a fake refund. Other handler failures retain retry/dead Inbox state for diagnosis.

**Phase 8 remains unimplemented.** No Payment module/entity/state machine/initiation/verification/gateway/business logic was created. The verified Payment → Order state + Outbox producer transaction and true Payment → Outbox → RabbitMQ → Inventory end-to-end test remain blocked by Phase 8. Order-created messaging is a real existing producer; Payment tests use contract events only. No new Order payment-state workflow, Wallet refund, Refund workflow, Redis, Shipping, Notification, Reporting or Phase 11 behavior was added.

## Configuration

`Messaging:Enabled` defaults to false, so application startup does not require SQL connectivity or a live broker for background workers. Business Outbox persistence still runs inside its business transaction when workers are disabled. Apply the reviewed Messaging migration before running such producers against a real database; this phase did not apply it.

PollingSeconds, BatchSize, MaxAttempts, RetrySeconds, MaximumRetrySeconds, LeaseSeconds, PublishTimeoutSeconds and MaximumPayloadBytes have bounded development defaults in appsettings.json. Lease duration must exceed publish timeout plus a safety margin. These settings and all RabbitMQ values can be overridden through configuration/environment.

Supply `Messaging__RabbitMq__Host`, `Port`, `Username`, `Password`, `VirtualHost`, `Exchange`, `DeadLetterExchange`, `UseTls` and `Prefetch` through secure external configuration. Host, port, credentials, virtual host and exchange/queue names have no deployable hardcoded fallback. TLS certificate validation uses the configured host.

Configure `Messaging:RabbitMq:Queues` with Queue, DeadLetterQueue, optional ConsumerName and RoutingKeys. Inventory consumer name is `inventory.payment-succeeded.v1`, with route `payments.succeeded.v1`. Provide durable bindings for `orders.created.v1` and `inventory.unavailable.v1` as well; queues for these future subscribers can omit ConsumerName and retain messages without adding a business consumer now. For example, environment keys `Messaging__RabbitMq__Queues__0__Queue`, `...__DeadLetterQueue`, `...__ConsumerName` and `...__RoutingKeys__0` configure the first binding. Names come from the deployment, not code. Workers validate their configured consumer/version binding.

Set credentials through environment/secret providers, never committed configuration. The adapter declares durable topic/direct exchanges and quorum queues in the supplied namespace; deploy matching topology and least-privilege broker/database access. It does not install RabbitMQ, launch Docker or provision a server.

## Validation and deferred infrastructure

Available tests use fake publishers/delivery stores, the actual Inventory application service with explicit test stores, and production policy/settlement logic. They verify outages, retry/dead behavior, stable duplicate publish IDs, deduplication decisions, stock=1/repeated payment events, clear failure contracts, no acknowledgement before processing completion, configuration/envelope validation, safe logs and immutable EF histories. Offline SQL model/migration checks block all connection attempts. These tests do not establish SQL or broker runtime guarantees.

Deferred SQL tests cover business + Outbox commit/rollback, exclusive claims and crash lease recovery, persisted retry limits, concurrent duplicate stock deduction + Inbox, all-line rollback/rejection events, Inbox persistence rollback and bounded consumer failures. Existing Order SQL tests also verify Order/Cart/Outbox atomicity. The deferred RabbitMQ test checks real broker confirmations, persistent versioned delivery, acknowledgement and unroutable publish failure. The true verified Payment producer end-to-end case is explicitly skipped because Phase 8 is missing.

Future authorized SQL tests use MESSAGING_TEST_SQL_SERVER and a GUID-named disposable MyOnlineShop_MessagingTests database. The fixture rejects LocalDB, migrates only the relevant contexts and validates its generated name before cleanup. Future broker tests use secure MESSAGING_TEST_RABBITMQ JSON configuration for a disposable authorized broker/vhost and touch only a generated test namespace. No SQL Server/LocalDB/RabbitMQ/Docker was installed or started, no persistent database was accessed/modified and no migration was applied during current validation.

InitialMessaging and its idempotent review script (`artifacts/messaging-migration.sql`, ignored generated artifact) create only shared messaging schema objects, constraints and indexes. Historical module migrations remain unchanged.

Implementation references: [RabbitMQ .NET client guide](https://www.rabbitmq.com/client-libraries/dotnet-api-guide), [publisher confirmation guide](https://www.rabbitmq.com/tutorials/tutorial-seven-dotnet), [RabbitMQ.Client package](https://www.nuget.org/packages/RabbitMQ.Client/7.2.2).
