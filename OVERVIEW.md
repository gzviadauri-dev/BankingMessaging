# BankingMessaging — Architecture & Design Overview

> A production-ready .NET 8 messaging sample built on **MassTransit 8** + **RabbitMQ** + **SQL Server**, modelling a real-world bank transfer workflow. This document explains the domain, every architectural pattern used, and the production best-practices applied throughout.

---

## Table of Contents

1. [What the project does](#1-what-the-project-does)
2. [High-level architecture](#2-high-level-architecture)
3. [Solution structure](#3-solution-structure)
4. [Message contract design](#4-message-contract-design)
5. [Patterns used — deep dive](#5-patterns-used--deep-dive)
   - 5.1 [Publisher / Consumer](#51-publisher--consumer)
   - 5.2 [Transactional Outbox](#52-transactional-outbox)
   - 5.3 [Idempotent Consumer (Inbox)](#53-idempotent-consumer-inbox)
   - 5.4 [Saga — State Machine Orchestration](#54-saga--state-machine-orchestration)
   - 5.5 [Dead Letter Queue (DLQ)](#55-dead-letter-queue-dlq)
   - 5.6 [Retry — Exponential Back-off](#56-retry--exponential-back-off)
   - 5.7 [Circuit Breaker](#57-circuit-breaker)
   - 5.8 [Optimistic Concurrency + Row-level Locking](#58-optimistic-concurrency--row-level-locking)
   - 5.9 [Quorum Queues](#59-quorum-queues)
   - 5.10 [Compensating Transactions](#510-compensating-transactions)
   - 5.11 [Saga Timeouts](#511-saga-timeouts)
6. [Production best practices](#6-production-best-practices)
7. [Observability stack](#7-observability-stack)
   - 7.1 [Signal pipeline](#signal-pipeline)
   - 7.2 [API setup (two lines)](#api-setup-two-lines)
   - 7.3 [Worker setup](#worker-setup-iservicecollection-extensions)
   - 7.4 [Three signals correlated](#three-signals-correlated-by-trace_id)
8. [End-to-end request flow](#8-end-to-end-request-flow)
9. [Failure scenarios and how they are handled](#9-failure-scenarios-and-how-they-are-handled)
10. [Technology decisions](#10-technology-decisions)

---

## 1. What the project does

The project models a **funds transfer** between two bank accounts. A client sends a single HTTP request to initiate a transfer; the system then:

1. Validates that the source account exists and has sufficient funds.
2. Debits the source account in a race-condition-safe way.
3. Credits the destination account.
4. Notifies the account holder that the transfer completed.
5. Persists the full audit trail at every step.
6. Handles every failure scenario without data loss or double-spend.

All communication between services happens through **durable RabbitMQ queues** — no in-process calls between the API, the transfer worker, and the notification worker. This means any service can be restarted, scaled out, or fail independently without losing work.

---

## 2. High-level architecture

```
┌─────────────────────────────────────────────────────────────────────┐
│                        CLIENT (curl / Swagger)                       │
└───────────────────────────────┬─────────────────────────────────────┘
                                │  POST /api/transfers
                                ▼
┌───────────────────────────────────────────────────────────────────────────┐
│  BankingMessaging.Api  (ASP.NET Core 8)                                    │
│                                                                            │
│   TransferController ──► IPublishEndpoint ──► Outbox Table (SQL Server)   │
│                                                      │                     │
│                                              MassTransit Outbox Relay      │
└──────────────────────────────────────────────────────┼────────────────────┘
                                                       │ (after DB commit)
                                                       ▼
                                           ┌───────────────────┐
                                           │     RabbitMQ       │
                                           │                    │
                                           │  Exchanges/Queues  │
                                           └─────────┬──────────┘
                          ┌──────────────────────────┼──────────────────────┐
                          │                          │                      │
                          ▼                          ▼                      ▼
          ┌───────────────────────┐    ┌───────────────────────┐  ┌──────────────────────┐
          │  TransferWorker        │    │  TransferWorker        │  │  NotificationWorker   │
          │                       │    │  (Saga State Machine)  │  │                      │
          │  InitiateTransfer     │    │                       │  │  SendNotification    │
          │  DebitAccount         │    │  TransferStateMachine │  │  Consumer            │
          │  CreditAccount        │    │                       │  │                      │
          │  DeadLetterConsumer   │    │                       │  │                      │
          └──────────┬────────────┘    └───────────┬───────────┘  └──────────────────────┘
                     │                             │
                     ▼                             ▼
          ┌──────────────────────────────────────────────────┐
          │                 SQL Server                         │
          │                                                    │
          │  Accounts   Transfers   InboxMessages              │
          │  OutboxMessages   TransferStates                   │
          │  MassTransit Outbox/Inbox tables                   │
          └────────────────────────────────────────────────────┘
```

### Infrastructure services

```
┌─────────────────┐   ┌───────────────────────────────────┐   ┌──────────────────┐
│  RabbitMQ        │   │  Graylog stack (opt-in profile)    │   │  Jaeger           │
│  :5672 / :15672  │   │                                   │   │  :16686 (UI)      │
│  Message broker  │   │  MongoDB  +  OpenSearch           │   │  Distributed      │
│  + Management UI │   │  + Graylog UI :9000               │   │  trace viewer     │
└─────────────────┘   │  (structured log aggregation)     │   │  (OTLP :4317)     │
                       └───────────────────────────────────┘   └──────────────────┘
```

> Start the full Graylog stack with `docker-compose --profile graylog up -d`, then set `Observability:Logging:Graylog:Enabled: true` in appsettings and restart the services.

---

## 3. Solution structure

```
BankingMessaging/
│
├── BankingMessaging.Back/                   ← .NET 8 backend solution
│   ├── src/
│   │   ├── BankingMessaging.Contracts/          ← Shared message contracts
│   │   │   ├── Commands/                        ← Intent messages (do X)
│   │   │   │   ├── InitiateTransferCommand.cs
│   │   │   │   ├── DebitAccountCommand.cs
│   │   │   │   ├── CreditAccountCommand.cs
│   │   │   │   └── SendNotificationCommand.cs
│   │   │   └── Events/                          ← Fact messages (X happened)
│   │   │       ├── TransferInitiatedEvent.cs
│   │   │       ├── AccountDebitedEvent.cs
│   │   │       ├── AccountCreditedEvent.cs
│   │   │       ├── TransferCompletedEvent.cs
│   │   │       ├── TransferFailedEvent.cs
│   │   │       ├── TransferDebitTimeout.cs      ← Saga timeout (debit step watchdog)
│   │   │       └── TransferCreditTimeout.cs     ← Saga timeout (credit step watchdog)
│   │   │   └── Versioning/
│   │   │       └── ContractVersioning.cs        ← Rules for safe rolling deployment
│   │   │
│   │   ├── BankingMessaging.Infrastructure/     ← Data layer (EF Core)
│   │   │   ├── Entities/                        ← EF Core entity models (rowversion on Account)
│   │   │   ├── Exceptions/                      ← Domain exceptions
│   │   │   ├── Persistence/                     ← DbContext + migrations
│   │   │   └── Sagas/                           ← State machine + timeouts + state entity
│   │   │
│   │   ├── BankingMessaging.Api/                ← HTTP entry point (publisher + /health)
│   │   │   ├── Controllers/
│   │   │   │   ├── AccountsController.cs        ← GET /api/accounts
│   │   │   │   └── TransferController.cs        ← POST/GET /api/transfers
│   │   │   ├── Models/InitiateTransferRequest.cs
│   │   │   └── Program.cs
│   │   │
│   │   ├── BankingMessaging.TransferWorker/     ← All transfer consumers
│   │   │   ├── Consumers/
│   │   │   │   ├── InitiateTransferConsumer.cs  ← Insert-and-catch inbox, validates, publishes
│   │   │   │   ├── DebitAccountConsumer.cs      ← Race-safe debit, rowversion, sets Debited
│   │   │   │   ├── CreditAccountConsumer.cs     ← Race-safe credit, rowversion, sets Completed
│   │   │   │   ├── CompensateDebitService.cs        ← Shared ReverseDebit logic (scoped DI)
│   │   │   │   ├── CompensateDebitFaultConsumer.cs  ← Path 1: Fault<CreditAccountCommand>
│   │   │   │   ├── CompensateDebitTimeoutConsumer.cs← Path 2: TransferFailedEvent{RequiresDebitReversal=true}
│   │   │   │   └── DeadLetterConsumer.cs        ← Handles initiate/debit faults, sets Failed
│   │   │   └── Program.cs                       ← MassTransit + Saga + DelayedScheduler + health
│   │   │
│   │   └── BankingMessaging.NotificationWorker/ ← Notification consumer
│   │       ├── Consumers/SendNotificationConsumer.cs
│   │       └── Program.cs
│   │
│   ├── tests/
│   │   └── BankingMessaging.IntegrationTests/
│   │       ├── Consumers/                       ← xUnit + MassTransit test harness
│   │       └── Infrastructure/                  ← Testcontainers fixture
│   │
│   ├── BankingMessaging.slnx
│   ├── nuget.config                             ← GitHub Packages (Company.Observability)
│   └── Makefile
│
├── BankingMessaging.Front/                  ← React 18 + TypeScript SPA
│   └── src/                                 ← TanStack Query · Recharts · Tailwind · Sonner
│
├── docker-compose.yml                       ← Infrastructure: RabbitMQ · SQL Server · Jaeger · Graylog
├── OVERVIEW.md
└── README.md
```

**Separation of concerns:**

| Layer | Responsibility | Dependencies |
|---|---|---|
| Contracts | Message shapes only — no logic | None |
| Infrastructure | EF Core, entities, saga state, exceptions | Contracts |
| Api | HTTP ingress, publish to outbox | Contracts + Infrastructure |
| TransferWorker | Process transfers, saga, DLQ | Contracts + Infrastructure |
| NotificationWorker | Deliver notifications | Contracts only |

The Notification Worker deliberately does **not** reference Infrastructure — it has zero database dependency.

---

## 4. Message contract design

Messages are split into two semantic categories:

### Commands — "please do X"
Sent to **one** consumer. Represent intent. Can be rejected.

```
InitiateTransferCommand   ─►  InitiateTransferConsumer
DebitAccountCommand        ─►  DebitAccountConsumer
CreditAccountCommand       ─►  CreditAccountConsumer
SendNotificationCommand    ─►  SendNotificationConsumer
```

### Events — "X has happened"
Published to **all** interested consumers. Represent facts. Cannot be rejected.

```
TransferInitiatedEvent  ─►  TransferStateMachine
AccountDebitedEvent     ─►  TransferStateMachine
AccountCreditedEvent    ─►  TransferStateMachine
TransferCompletedEvent  ─►  (audit / analytics consumers)
TransferFailedEvent     ─►  TransferStateMachine
                        ─►  CompensateDebitTimeoutConsumer
                               (acts only when RequiresDebitReversal=true)
```

`TransferFailedEvent` carries a `RequiresDebitReversal` boolean (default `false`) that encodes the saga's state at failure time:

| Failure origin | `RequiresDebitReversal` | Meaning |
|---|---|---|
| `DebitTimeout` fires (`Initiated` state) | `false` | Debit never happened — no reversal needed |
| Any failure in `Initiated` state | `false` | Saga never left `Initiated` — source account untouched |
| `CreditTimeout` fires (`Debited` state) | `true` | Debit happened — reversal required |
| `Fault<CreditAccountCommand>` processed | `false` | Compensation already done by `CompensateDebitFaultConsumer` — reversal not needed again |

The `false` default ensures old producers that don't include this field can never accidentally trigger compensation on a consumer that was updated before the producer.

### Timeout messages — "X has not happened within N minutes"

Scheduled by the saga using MassTransit's `Schedule<>` API. If the expected event never arrives, the timeout fires and the saga self-heals.

```
TransferDebitTimeout  ─►  TransferStateMachine  (fires 5 min after DebitAccountCommand if AccountDebitedEvent never arrives)
TransferCreditTimeout ─►  TransferStateMachine  (fires 5 min after CreditAccountCommand if AccountCreditedEvent never arrives)
```

### Contract versioning

All contracts follow the rules in `ContractVersioning.cs`:
- Never remove or rename a property
- New fields must be nullable or carry a safe default
- Breaking changes create a V2 record alongside the V1

MassTransit 8.x System.Text.Json serializer ignores unknown fields by default, enabling safe rolling deployment when producers and consumers are updated independently.

Every message carries both `TransferId` (the business entity) and `CorrelationId` (the saga instance), enabling end-to-end trace correlation across all log entries and distributed spans.

---

## 5. Patterns used — deep dive

### 5.1 Publisher / Consumer

The fundamental messaging pattern. Services communicate exclusively through RabbitMQ — never through direct method calls or shared memory.

```
  Producer                   Broker                  Consumer
  ────────                   ──────                  ────────
  API publishes          ┌─────────────┐         Worker subscribes
  InitiateTransfer  ───► │  Exchange   │ ───────► to queue and
  Command                │   (topic)   │          processes message
                         └──────┬──────┘
                                │ routes to
                         ┌──────▼──────┐
                         │   Queue     │  ← durable, survives restart
                         └─────────────┘
```

**Why:** Decouples the API from processing logic. The API returns `202 Accepted` immediately without waiting for the transfer to complete. Processing happens asynchronously at the consumer's own pace.

---

### 5.2 Transactional Outbox

**Problem:** Saving to DB and publishing to RabbitMQ are two separate operations. A crash between them leaves the system in an inconsistent state — the record is saved but the message is never sent (or vice versa).

**Solution:** Write the message to an `OutboxMessages` table in the *same database transaction* as the business data. A background relay then reads unsent outbox rows and delivers them to RabbitMQ, marking them `SentAt` on success.

```
  API Handler (single DB transaction)
  ─────────────────────────────────────────────────────────
  BEGIN TRANSACTION
    INSERT INTO Transfers (Status = 'Pending')     ← business row
    INSERT INTO OutboxMessages (Payload = command) ← message row
  COMMIT
  ─────────────────────────────────────────────────────────

  MassTransit Outbox Relay (background)
  ─────────────────────────────────────
  SELECT unsent rows FROM OutboxMessages
  Publish to RabbitMQ
  UPDATE OutboxMessages SET SentAt = NOW()
```

**Guarantee:** Either both the business row and the message are committed, or neither is. The message will always reach RabbitMQ — even if the API process crashes mid-flight.

Implemented with: `AddEntityFrameworkOutbox<BankingDbContext>(o => { o.UseSqlServer(); o.UseBusOutbox(); })`

---

### 5.3 Idempotent Consumer (Inbox)

**Problem:** RabbitMQ provides *at-least-once* delivery. A message can be redelivered if the consumer crashes after processing but before acknowledging. Processing twice would double-debit an account.

A naïve check-then-insert approach has a **TOCTOU (Time-Of-Check-Time-Of-Use) race condition**: two concurrent redeliveries can both pass the `AnyAsync` check before either inserts the row — both then process the message.

**Solution:** Attempt the `InboxMessages` INSERT first, and let the database's PK unique constraint be the atomic guard. Exactly one concurrent INSERT wins; all others hit the constraint and exit silently.

```
Consumer receives message
         │
         ▼
  ┌──────────────────────────────────────────┐
  │  _db.InboxMessages.Add(inboxEntry)        │
  │  await _db.SaveChangesAsync()             │
  │  ← the PK unique constraint is the guard  │
  └────────────┬─────────────────────────────┘
               │
  ┌────────────┴──────────────────────┐
  │  DbUpdateException?                │
  └────┬──────────────────────────┬───┘
       │ SqlException 2627/2601    │ no exception
       ▼                           ▼
  Duplicate — SKIP             Process business logic
  (ChangeTracker.Clear()       (inboxEntry.ProcessedAt = UtcNow)
   return)                     SaveChanges
                               Publish next message
```

```csharp
// InitiateTransferConsumer.cs
try
{
    _db.InboxMessages.Add(inboxEntry);
    await _db.SaveChangesAsync(context.CancellationToken);
}
catch (DbUpdateException ex) when (IsDuplicateKeyException(ex))
{
    _logger.LogWarning("Duplicate {MessageId} — skipping", messageId);
    _db.ChangeTracker.Clear();
    return;
}

// Business logic only reached once per MessageId
...

private static bool IsDuplicateKeyException(DbUpdateException ex) =>
    ex.InnerException is SqlException sqlEx && sqlEx.Number is 2627 or 2601;
```

**Guarantee:** Consuming the same message N times produces exactly the same outcome as consuming it once, even under concurrent redelivery to multiple consumer instances.

---

### 5.4 Saga — State Machine Orchestration

**Problem:** A transfer is a *multi-step workflow* spanning several messages and services. Without a coordinator, there is no single place that knows the overall state of a transfer.

**Solution:** A MassTransit Saga (`TransferStateMachine`) acts as the orchestrator. It stores the current state of each transfer instance in the `TransferStates` table and decides what to do next when each event arrives.

#### State transition diagram

```
                            ┌─────────────────────────┐
                            │         Initial          │
                            └────────────┬────────────┘
                                         │ TransferInitiatedEvent
                                         │ (publishes DebitAccountCommand)
                                         ▼
                            ┌─────────────────────────┐
                            │        Initiated         │
                            └────────────┬────────────┘
                       ┌─────────────────┴───────────────────┐
                       │ AccountDebitedEvent                  │ TransferFailedEvent
                       │ (publishes CreditAccountCommand)     │
                       ▼                                      ▼
           ┌───────────────────────┐            ┌────────────────────────┐
           │        Debited        │            │         Failed         │
           └───────────┬───────────┘            │     (terminal)         │
            ┌──────────┴──────────┐             └────────────────────────┘
            │ AccountCreditedEvent│
            │ (publishes          │
            │  TransferCompleted  │
            │  + Notification)    │
            ▼                     │ TransferFailedEvent
┌───────────────────────┐         ▼
│       Completed        │  ┌─────────────────┐
│     (terminal)         │  │     Failed       │
└────────────────────────┘  │   (terminal)     │
                            └─────────────────┘
```

#### Why a Saga instead of chained consumers?

| Approach | Problem |
|---|---|
| Chained events only | No central state — impossible to know if a transfer is "Debited but not yet Credited" |
| Direct service calls | Tight coupling, synchronous, no fault tolerance |
| **Saga (chosen)** | Single source of truth for workflow state, handles partial failures, durable |

Each saga instance is persisted with **optimistic concurrency** (`RowVersion` byte array → SQL Server `rowversion` column) to prevent two concurrent event deliveries from corrupting the same instance.

#### Timeout scheduling — self-healing saga

A saga that publishes a command and then waits indefinitely is a **frozen saga** — the transfer is stuck with no resolution. To prevent this, the state machine schedules a watchdog timeout after each command:

```csharp
// TransferStateMachine.cs
Schedule(() => DebitTimeout,
    state => state.DebitTimeoutTokenId,   // stores the cancellation token
    s =>
    {
        s.Delay    = TimeSpan.FromMinutes(5);
        s.Received = r => r.CorrelateById(m => m.Message.CorrelationId);
    });

// In Initially — schedule watchdog AFTER publishing DebitAccountCommand
.Publish(ctx => new DebitAccountCommand { ... })
.Schedule(DebitTimeout, ctx => new TransferDebitTimeout
{
    CorrelationId = ctx.Saga.CorrelationId,
    TransferId    = ctx.Saga.TransferId
})
.TransitionTo(Initiated)

// In During(Initiated) — cancel watchdog when AccountDebitedEvent arrives
When(AccountDebited)
    .Unschedule(DebitTimeout)   // debited in time — cancel the 5-min timer
    .Publish(ctx => new CreditAccountCommand { ... })
    .Schedule(CreditTimeout, ...)
    ...

// Also in During(Initiated) — handle the timeout if AccountDebitedEvent never arrived.
// RequiresDebitReversal=false: saga is in Initiated state, debit never happened.
When(DebitTimeout!.Received)
    .Then(ctx => ctx.Saga.FailureReason = "Debit timed out after 5 minutes")
    .Publish(ctx => new TransferFailedEvent
    {
        // ...
        RequiresDebitReversal = false   // ← debit never happened, no reversal needed
    })
    .TransitionTo(Failed)
    .Finalize()

// In During(Debited) — credit timeout: debit DID happen, reversal required.
// RequiresDebitReversal=true triggers CompensateDebitTimeoutConsumer to reverse the debit.
When(CreditTimeout!.Received)
    .Publish(ctx => new TransferFailedEvent
    {
        // ...
        RequiresDebitReversal = true    // ← debit happened, MUST reverse
    })
    .TransitionTo(Failed)
    .Finalize()
```

**Requires:** `cfg.UseDelayedMessageScheduler()` in `Program.cs` and the `rabbitmq_delayed_message_exchange` plugin in RabbitMQ (the `docker-compose.yml` uses `heidiks/rabbitmq-delayed-message-exchange:3.13.7-management` which has it pre-installed).

---

### 5.5 Dead Letter Queue (DLQ)

**Problem:** Some messages will fail permanently (e.g., account not found). After exhausting retries, the message must not be silently dropped — it must be captured and acted upon.

**Solution:** MassTransit automatically moves exhausted messages to `<queue-name>_error` queues. The `DeadLetterConsumer` subscribes to `Fault<T>` envelopes for each command type and marks the corresponding `Transfer` row as `Failed` in the database.

```
  Normal flow:
  ─────────────────────────────────────────────────────
  Message → Consumer → SUCCESS → ACK → removed from queue

  Failure flow (with retries exhausted):
  ─────────────────────────────────────────────────────
  Message → Consumer → EXCEPTION
              │
              ▼
         Retry logic
         (attempt 1, 2, … N)
              │ all failed
              ▼
   transfer-initiate_error queue  ◄── MassTransit automatic routing
              │
              ▼
   DeadLetterConsumer
   (handles Fault<InitiateTransferCommand>)
              │
              ▼
   UPDATE Transfers SET Status = 'Failed', FailureReason = '...'
```

The error queue is visible in the RabbitMQ Management UI at `http://localhost:15672`. Messages there can be inspected, replayed, or purged manually.

---

### 5.6 Retry — Exponential Back-off

Different queues use different retry strategies tuned to their workload:

```
  transfer-initiate queue
  ────────────────────────────────────────────────────────────
  Attempt 1 → fail → wait  2s
  Attempt 2 → fail → wait  7s   (2 + 5)
  Attempt 3 → fail → wait 12s   (2 + 5 + 5)
  Attempt 4 → fail → wait 17s
  Attempt 5 → fail → wait 22s
  → DLQ

  transfer-debit / transfer-credit queues
  ────────────────────────────────────────────────────────────
  Attempt 1 → fail → wait  1s
  Attempt 2 → fail → wait  4s
  Attempt 3 → fail → DLQ

  notification-queue
  ────────────────────────────────────────────────────────────
  Attempt 1 → fail → wait  5s (fixed intervals)
  Attempt 2 → fail → wait 15s
  Attempt 3 → fail → wait 60s
  → DLQ
```

**Business exceptions bypass retries entirely:**

```csharp
r.Ignore<InsufficientFundsException>();   // Business rule violated — retrying won't help
r.Ignore<AccountNotFoundException>();     // Account doesn't exist — retrying won't help
r.Handle<TransientDatabaseException>();   // Transient — worth retrying
```

This prevents retrying exceptions that will never succeed (wasting time and resources) while still retrying recoverable infrastructure failures.

---

### 5.7 Circuit Breaker

**Problem:** If a downstream dependency (e.g., SQL Server) is unhealthy, consumers will fail fast and generate a flood of retry noise. Left unchecked, this exhausts thread pools.

**Solution:** A circuit breaker tracks the rolling failure rate. When failures exceed the threshold, it "trips" — all subsequent messages are rejected immediately (without even attempting processing) for a reset interval.

```
  CLOSED (normal)                   OPEN (tripped)
  ───────────────                   ──────────────
  Messages flow normally.           All messages rejected instantly.
  Failure rate tracked.             No processing attempted.
  
        │                                 │
        │ failure rate > 15%              │ reset interval (5 min) expires
        │ AND at least 10 messages seen   │
        ▼                                 ▼
   OPEN (tripped)               HALF-OPEN (probe)
                                 ───────────────
                                 One message allowed through.
                                 ┌── succeeds → back to CLOSED
                                 └── fails    → back to OPEN
```

Configuration in this project:
- Tracking period: 1 minute rolling window
- Trip threshold: 15% failure rate
- Minimum observations: 10 messages (avoids tripping on 1/1 = 100%)
- Reset interval: 5 minutes

---

### 5.8 Optimistic Concurrency + Row-level Locking

**Problem:** Multiple consumers process messages concurrently. Without protection, two consumers can both read the same account balance, both compute `balance - amount`, and both write the same reduced value — a classic double-spend race condition.

```
  WITHOUT protection (broken):
  ──────────────────────────────────────────────────────────────────
  Consumer A reads  balance = 1000   Consumer B reads  balance = 1000
  Consumer A writes balance = 800    Consumer B writes balance = 800
                                     ↑ WRONG — B overwrote A's debit!
  Net effect: only 200 was debited instead of 400
```

**Three-layer defence used in this project:**

---

#### Layer 1 — `RepeatableRead` Transaction Isolation

Every debit/credit operation opens a database transaction at `RepeatableRead` isolation level. This ensures that once a row is read inside a transaction, no other transaction can modify it until the first one completes.

```csharp
// DebitAccountConsumer.cs — DebitAccountConsumer / CreditAccountConsumer
await using var transaction = await _db.Database.BeginTransactionAsync(
    IsolationLevel.RepeatableRead, context.CancellationToken);
```

`RepeatableRead` prevents non-repeatable reads: if Consumer A reads balance=1000 inside this isolation level, any subsequent read of the same row within the same transaction is guaranteed to return 1000 — even if Consumer B committed a change in between.

---

#### Layer 2 — Row-level Write Lock (`WITH (UPDLOCK, ROWLOCK)`)

Inside the transaction, the account row is read with SQL Server lock hints that immediately acquire an exclusive write lock — before any modification is made.

```csharp
// DebitAccountConsumer.cs
var account = await _db.Accounts
    .FromSqlRaw(
        "SELECT * FROM Accounts WITH (UPDLOCK, ROWLOCK) WHERE AccountId = {0}",
        msg.AccountId)
    .FirstOrDefaultAsync(context.CancellationToken)
    ?? throw new AccountNotFoundException(msg.AccountId);
```

- **`UPDLOCK`** — acquires an update lock (upgrade-intent) on read, preventing deadlocks when two transactions try to upgrade a shared lock to exclusive simultaneously.
- **`ROWLOCK`** — locks only the specific row, not the whole page or table, maximising concurrency for accounts that are not involved in the same transfer.

**What happens when two consumers race:**

```
  Consumer A (Debit $200)                Consumer B (Debit $300)
  ─────────────────────────────          ─────────────────────────────
  BEGIN RepeatableRead                   BEGIN RepeatableRead
  SELECT WITH (UPDLOCK, ROWLOCK)         SELECT WITH (UPDLOCK, ROWLOCK)
    AccountId = 'ACC-001'                  AccountId = 'ACC-001'
    → LOCK ACQUIRED ✓                      → BLOCKED (waiting for A)

  balance = 1000
  balance -= 200  →  800
  (SQL Server auto-increments rowversion)
  SaveChanges ✓
  COMMIT → lock released

                                         → UNBLOCKED (lock acquired)
                                         balance = 800   ← reads updated value
                                         balance -= 300  →  500
                                         (SQL Server auto-increments rowversion)
                                         SaveChanges ✓
                                         COMMIT

  ✓ Final balance: 500  (correct — exactly $500 debited in total)
```

---

#### Layer 3 — Optimistic Concurrency (SQL Server `rowversion` token + Application Retry Loop)

`Account.RowVersion` is a SQL Server **`rowversion`** column (8-byte binary, equivalent to `timestamp`). SQL Server increments it atomically on **every UPDATE** — no application code touches it. EF Core maps it with `.IsRowVersion().IsConcurrencyToken()` which automatically includes it in every `UPDATE` and `DELETE` WHERE clause.

```csharp
// Account.cs
// SQL Server rowversion — DB-managed, never set or incremented in application code.
public byte[] RowVersion { get; set; } = null!;

// BankingDbContext.cs
e.Property(a => a.RowVersion)
    .IsRowVersion()        // maps to SQL Server 'rowversion' type
    .IsConcurrencyToken(); // EF Core adds it to UPDATE/DELETE WHERE clause
```

```sql
-- What EF Core generates on SaveChanges (automatically)
UPDATE Accounts
SET Balance = @newBalance, UpdatedAt = @now
WHERE AccountId = @id AND RowVersion = @expectedRowVersion
-- SQL Server auto-increments RowVersion on the row if the UPDATE succeeds
-- If 0 rows affected → DbUpdateConcurrencyException
```

**Why `rowversion` over a manually-incremented `long`?**

A manually incremented `long` has a critical flaw: two consumers can both read `RowVersion=5`, both compute `6`, and both include `WHERE RowVersion=5` — the second write **succeeds** if the first committed between the read and write, because the WHERE clause still matches `5`. Only an atomic, DB-managed counter eliminates this race at the database level.

When two transactions slip through simultaneously (e.g., under brief lock timeout or network jitter), the second `SaveChanges` finds the `rowversion` has already advanced — it throws `DbUpdateConcurrencyException`. The consumer catches this, detaches **only the stale Account entity**, and retries the full read-lock-modify-write cycle with fresh data:

```csharp
// DebitAccountConsumer.cs — full retry loop
const int maxAttempts = 3;
for (int attempt = 1; attempt <= maxAttempts; attempt++)
{
    await using var transaction = await _db.Database.BeginTransactionAsync(
        IsolationLevel.RepeatableRead, context.CancellationToken);
    try
    {
        var account = await _db.Accounts
            .FromSqlRaw(
                @"SELECT * FROM Accounts WITH (UPDLOCK, ROWLOCK)
                  WHERE AccountId = {0} AND IsDeleted = 0",
                msg.AccountId)
            .FirstOrDefaultAsync(context.CancellationToken)
            ?? throw new AccountNotFoundException(msg.AccountId);

        account.Balance -= msg.Amount;
        // RowVersion is NOT touched here — SQL Server increments it on the UPDATE automatically
        account.UpdatedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(context.CancellationToken);  // throws if rowversion mismatch
        await transaction.CommitAsync(context.CancellationToken);

        await _publish.Publish(new AccountDebitedEvent { ... });
        return;
    }
    catch (DbUpdateConcurrencyException ex) when (attempt < maxAttempts)
    {
        await transaction.RollbackAsync(context.CancellationToken);

        // Only detach the stale Account entity — do NOT clear the whole change tracker.
        // ChangeTracker.Clear() would also wipe InboxMessage and Transfer status updates
        // that were already saved in previous SaveChanges calls.
        foreach (var entry in ex.Entries)
        {
            if (entry.Entity is Account)
                entry.State = EntityState.Detached;
        }

        await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), context.CancellationToken);
    }
}

throw new ConcurrencyException(
    $"Failed to debit account {msg.AccountId} after {maxAttempts} attempts.");
```

**Why per-entity detach instead of `ChangeTracker.Clear()`?**

`ChangeTracker.Clear()` wipes **all tracked entities**. In these consumers, the `InboxMessage` is inserted in a separate `SaveChanges` *before* the retry loop. Clearing everything on retry would cause the inbox INSERT to be re-attempted, hit the unique PK constraint, and throw a false duplicate error — masking the real concurrency problem. Detaching only the stale `Account` entity leaves the inbox record safely tracked.

**RowVersion conflict timeline:**

```
  Consumer A reads  balance=1000, rowversion=0x0000000000000005
  Consumer B reads  balance=1000, rowversion=0x0000000000000005   ← both read before either writes

  Consumer A: UPDATE ... WHERE RowVersion=0x05 → 1 row affected ✓  (DB sets RowVersion to 0x06)
  Consumer B: UPDATE ... WHERE RowVersion=0x05 → 0 rows affected ✗ → DbUpdateConcurrencyException
                                                                            │
                                                                            ▼
                                                                  RollbackAsync
                                                                  Detach Account entity only
                                                                  Task.Delay(50ms)
                                                                  ── retry attempt 2 ──
                                                                  re-read balance=800, rowversion=0x06
                                                                  UPDATE WHERE RowVersion=0x06 → 1 row ✓
```

---

#### How all three layers interact

```
  5 simultaneous consumers, all targeting ACC-001 (balance = 1000, debit $50 each)
  ─────────────────────────────────────────────────────────────────────────────────

  Layer 1 (RepeatableRead) + Layer 2 (UPDLOCK):
    Consumer 1 acquires UPDLOCK      → reads 1000, debits → 950, SQL Server increments rowversion, COMMIT
    Consumers 2–5 blocked by lock    → unblock one at a time, each reads updated balance
    Consumer 2 acquires UPDLOCK      → reads  950, debits →  900, SQL Server increments rowversion, COMMIT
    Consumer 3 acquires UPDLOCK      → reads  900, debits →  850, SQL Server increments rowversion, COMMIT
    Consumer 4 acquires UPDLOCK      → reads  850, debits →  800, SQL Server increments rowversion, COMMIT
    Consumer 5 acquires UPDLOCK      → reads  800, debits →  750, SQL Server increments rowversion, COMMIT

  Layer 3 (RowVersion) catches any that slipped through:
    If two consumers somehow both passed the lock at the same instant,
    the second SaveChanges fails → DbUpdateConcurrencyException → retry with fresh read.

  ✓ Final balance: 750  (exactly 5 × $50 = $250 debited, never negative, no double-spend)
```

**Why three layers instead of one?**

| Layer | Prevents | Limitation |
|---|---|---|
| `RepeatableRead` | Non-repeatable reads within a transaction | Doesn't block *writes* from other transactions by itself |
| `UPDLOCK + ROWLOCK` | Concurrent writes to the same row | Relies on all writers using the same hint; doesn't help at application layer |
| `RowVersion` + retry | Any write that slipped through the other two | Only catches the problem *after* the fact; requires a retry |

Each layer fills the gap the previous one leaves. Together they provide a defence-in-depth that is both correct and efficient (row-level locking keeps unrelated accounts unaffected).

#### Raw SQL — EF Core global filter bypass warning

Because `FromSqlRaw` bypasses EF Core's global query filters, the `IsDeleted = 0` predicate must be manually included in every raw SQL query that targets `Account`. All consumers that use `UPDLOCK + ROWLOCK` — `DebitAccountConsumer`, `CreditAccountConsumer`, and `CompensateDebitService` — carry an identical warning comment block:

```csharp
// ⚠️  RAW SQL — EF CORE GLOBAL FILTERS ARE BYPASSED HERE ⚠️
// This query manually replicates ALL active global filters on the Account entity.
// UPDLOCK + ROWLOCK hints cannot be applied through EF Core's LINQ pipeline.
//
// IF YOU:
//   • Rename the IsDeleted column → update the SQL in all three consumers
//   • Add a new global filter to Account (e.g. TenantId, IsArchived) → add it here too
//   • Remove soft-delete from Account → remove the IsDeleted condition
//
// SEARCH: "RAW SQL — EF CORE GLOBAL FILTERS ARE BYPASSED" to find all locations.
var account = await _db.Accounts
    .FromSqlRaw(
        @"SELECT * FROM Accounts WITH (UPDLOCK, ROWLOCK)
          WHERE AccountId = {0}
            AND IsDeleted = 0",   // ← manually replicates soft-delete global filter
        msg.AccountId)
    .FirstOrDefaultAsync(ct);
```

This makes the bypass visible at the point of use and provides an exact search term to locate every affected query during future schema changes.

---

### 5.9 Quorum Queues

**Problem:** RabbitMQ's default Classic queues store messages only on a single node. If that node's disk fails before the message is consumed, the message is lost — unacceptable for a banking system.

**Solution:** All queues in this project are declared as **Quorum queues** — RabbitMQ's modern, Raft-consensus-based queue type. Every message is written to a majority quorum of cluster nodes before the broker acknowledges receipt.

```
  Classic queue (default):           Quorum queue (this project):
  ─────────────────────────          ────────────────────────────────────────
  Single node storage.               Raft log replicated across N nodes.
  Node failure → messages lost.      Node failure → leader election, no loss.
  No memory safety guarantees.       Dead-letter limit via x-delivery-limit.
  Suitable for dev/low-risk.         Recommended for production finance.
```

**Configuration applied to every endpoint:**

```csharp
cfg.ReceiveEndpoint("transfer-initiate", e =>
{
    e.SetQueueArgument("x-queue-type", "quorum");
    // ... retry, concurrency, consumer config ...
});
```

This covers all 6 queues:

| Queue | Worker | Purpose |
|---|---|---|
| `transfer-initiate` | TransferWorker | Entry point for new transfer commands |
| `transfer-debit` | TransferWorker | Account debit processing |
| `transfer-credit` | TransferWorker | Account credit processing |
| `transfer-fault-handler` | TransferWorker | Dead-letter / fault handling |
| `TransferState` | TransferWorker | Saga state machine inbox |
| `notification-queue` | NotificationWorker | Send notification commands |

**Important constraints for Quorum queues:**
- Cannot be converted from Classic — must be declared fresh (recreated on first startup)
- Require at least 1 node to form quorum (3 for full fault tolerance in production)
- Do not support `x-message-ttl` on the queue level — use per-message TTL instead
- Per-message delivery limit (`x-delivery-limit`) replaces the Classic `x-max-delivery-count`

> In local Docker (single node), you get WAL-based persistence and no message loss on container restart — but not multi-node replication. Run a 3-node RabbitMQ cluster in production to get full quorum guarantees.

---

### 5.10 Compensating Transactions

**Problem:** The saga's two-phase workflow (Debit → Credit) is not atomic across both accounts. If the debit succeeds but the credit fails permanently (all retries exhausted, or credit timeout fires), the source account has been debited without the destination being credited — money disappears.

**Solution:** Two focused consumer classes share a `CompensateDebitService` that owns the reversal logic:

- **Path 1 — `CompensateDebitFaultConsumer`** handles `Fault<CreditAccountCommand>`: MassTransit auto-publishes this after the credit endpoint exhausts its retry policy. The consumer reverses the debit, then publishes `TransferFailedEvent{RequiresDebitReversal=false}` so the saga (still in `Debited` state) can finalize.

- **Path 2 — `CompensateDebitTimeoutConsumer`** handles `TransferFailedEvent{RequiresDebitReversal=true}`: Published by the saga when `CreditTimeout` fires while in the `Debited` state. The consumer checks the flag, reverses the debit, and does **not** re-publish (the saga already transitioned to `Failed` before publishing this event).

#### Why two classes instead of one?

If a single class implemented both `IConsumer<Fault<CreditAccountCommand>>` and `IConsumer<TransferFailedEvent>`, MassTransit would bind the endpoint queue to **both** exchanges. Every `TransferFailedEvent` would also arrive at the fault-handler queue, and every `Fault<CreditAccountCommand>` at the timeout-handler queue — each message processed **twice**. Two concurrent `ReverseDebit` calls could race even with the `UPDLOCK` guard: UPDLOCK serialises them, but the second call would still run the whole method before seeing `Status == "Failed"`.

Separate classes give each endpoint queue **exactly one exchange binding** and eliminate the concurrency surface entirely.

#### Why two separate trigger paths?

A single `TransferFailedEvent` is published from **multiple** saga states:

```
  During(Initiated) → DebitTimeout fires  →  RequiresDebitReversal = false
                                               (debit NEVER happened — no reversal)
  During(Debited)   → CreditTimeout fires →  RequiresDebitReversal = true
                                               (debit DID happen — reversal required)
```

Subscribing to a plain `TransferFailedEvent` without checking the flag would cause **phantom credits** — reversing a debit that never occurred — whenever a debit timeout fires. The `RequiresDebitReversal` field makes the saga's state explicit in the message itself.

#### Flow — Path 1 (credit retries exhausted)

```
  CreditAccountConsumer
    └─ all retries exhausted
          │  MassTransit auto-publishes
          ▼
  Fault<CreditAccountCommand>
          │  routes to transfer-credit-fault-handler
          ▼
  CompensateDebitFaultConsumer.Consume<Fault<CreditAccountCommand>>
    └─ CompensateDebitService.ReverseDebit(transferId, reason, ct)
         ├─ BEGIN TRANSACTION (RepeatableRead)
         ├─ SELECT * FROM Transfers WITH (UPDLOCK, ROWLOCK) WHERE TransferId = ?  ← serialises concurrent compensation
         ├─ Guard: transfer.Status == TransferStatus.Debited?  ← idempotency check
         │    → not "Debited" (already compensated) → skip
         ├─ SELECT source account WITH (UPDLOCK, ROWLOCK)      ← raw SQL, global filters bypassed
         ├─ sourceAccount.Balance += transfer.Amount           ← amount from locked DB row
         ├─ transfer.Status = TransferStatus.Failed
         ├─ SaveChangesAsync()                                 ← ONE call — both changes atomic
         └─ COMMIT
    └─ IF reversal performed: Publish TransferFailedEvent{RequiresDebitReversal=false}
          │
          ▼
  TransferStateMachine During(Debited) When(TransferFailed)
    ├─ Unschedule(CreditTimeout)
    └─ State: Debited → Failed
```

#### Flow — Path 2 (saga credit timeout)

```
  CreditTimeout fires (5 min, no AccountCreditedEvent)
          │
          ▼
  TransferStateMachine During(Debited) When(CreditTimeout.Received)
    ├─ Publish TransferFailedEvent{RequiresDebitReversal=true}
    └─ State: Debited → Failed

          │  routes to transfer-failed-compensation-handler
          ▼
  CompensateDebitTimeoutConsumer.Consume<TransferFailedEvent>
    ├─ Guard: RequiresDebitReversal=true? → proceed (skip if false)
    └─ CompensateDebitService.ReverseDebit(transferId, reason, ct)
         ├─ BEGIN TRANSACTION (RepeatableRead)
         ├─ SELECT * FROM Transfers WITH (UPDLOCK, ROWLOCK)    ← serialises concurrent compensation
         ├─ Guard: transfer.Status == TransferStatus.Debited?  ← idempotency check
         ├─ SELECT source account WITH (UPDLOCK, ROWLOCK)
         ├─ sourceAccount.Balance += transfer.Amount
         ├─ transfer.Status = TransferStatus.Failed
         ├─ SaveChangesAsync()                                 ← ONE call, atomic
         └─ COMMIT
    [NO re-publish — saga already transitioned to Failed before publishing this event]
```

#### Atomicity — partial failure gap

A critical subtlety: if the process crashes **after** `SaveChangesAsync` but **before** `CommitAsync`, the transaction is rolled back. On redelivery, `transfer.Status` is still `"Debited"` — the compensation runs again. This is correct:

```
Crash window:
  SaveChangesAsync() ✓   (balance restored, status = Failed in transaction)
  CommitAsync()      ✗   (transaction rolled back → balance NOT actually restored)
  ← process restarts, receives same message ─►
  transfer.Status == "Debited" → compensation runs again → correct
```

Both the balance update and the status change are in **one `SaveChangesAsync` call** before `CommitAsync`. There is no intermediate save between them. This means there is no state where balance is restored but status is still `"Debited"` (or vice versa) after a rollback.

The idempotency guard is `transfer.Status == "Debited"` — not `!= "Failed"` — because this is the only state that requires reversal:

```
Pending   → debit never ran       → no reversal needed
Debited   → debit happened        → reversal required  ← only state we act on
Completed → credit succeeded      → NEVER reverse
Failed    → already compensated   → skip (idempotent re-delivery)
```

**Key design decisions:**

| Decision | Reason |
|---|---|
| `RequiresDebitReversal` field instead of a separate event type | A separate `DebitReversalRequired` event would require a new contract. Adding a boolean field to the existing `TransferFailedEvent` is backward-compatible — old producers omit the field and the default `false` is safe. |
| Two separate endpoints (`transfer-credit-fault-handler` + `transfer-failed-compensation-handler`) | Each endpoint is independently rate-limited with `PrefetchCount=1, ConcurrencyLimit=1`. Separation also makes queue monitoring explicit in RabbitMQ Management UI. |
| `ReverseDebit` returns `bool` | Only publishes `TransferFailedEvent` (path 1) if the reversal was actually performed. Prevents duplicate saga finalization events if the consumer is re-delivered after a partial failure. |
| 5 internal retry attempts (vs 3 for normal consumers) | Money is at stake — try harder before escalating. |
| No broker-level retry on either endpoint | The internal retry loop handles concurrency conflicts. A broker retry would rerun the entire consumer, risking double-compensation before the previous attempt's transaction commits. |
| `CRITICAL` log level on compensation failure | If compensation itself fails after 5 attempts, a human must intervene. The log alert is the escalation path. |
| `UPDLOCK + ROWLOCK` with `IsDeleted = 0` guard in raw SQL | Same three-layer defence as normal debit/credit. Comment `⚠️ RAW SQL — EF CORE GLOBAL FILTERS ARE BYPASSED` marks the location. |

---

### 5.11 Saga Timeouts

**Problem:** A saga that publishes a command waits indefinitely for the response event. If the consumer crashes permanently, the message is lost, and the saga stays in `Initiated` or `Debited` state forever — a **frozen transfer** with no resolution path.

**Solution:** After publishing each command, the saga schedules a watchdog timeout using MassTransit's `Schedule<>` API. If the expected event arrives in time, the timeout is cancelled (`Unschedule`). If the event never arrives, the timeout fires and the saga self-heals.

```
Saga publishes DebitAccountCommand
      │
      ├── [schedules TransferDebitTimeout in 5 min]
      │
      ▼ (two possible continuations)

Continuation A — AccountDebitedEvent arrives in time:
      Saga calls Unschedule(DebitTimeout)   ← timer cancelled, no-op
      → normal happy path continues

Continuation B — 5 minutes pass with no AccountDebitedEvent:
      TransferDebitTimeout fires
      → Saga: Initiated → Failed
      → Publishes TransferFailedEvent
```

**Credit timeout also triggers compensation:**

```
TransferCreditTimeout fires (AccountCreditedEvent never arrived)
      │
      ▼
Saga publishes TransferFailedEvent{RequiresDebitReversal=true}
  (saga was in Debited state — debit DID happen, reversal required)
      │
      ▼
CompensateDebitConsumer.Consume<TransferFailedEvent>
      ├─ RequiresDebitReversal=true → proceed
      └─ Reverses the debit (source account was already debited)
         [No re-publish — saga already transitioned to Failed above]
```

**Debit timeout — no phantom compensation:**

```
TransferDebitTimeout fires (AccountDebitedEvent never arrived)
      │
      ▼
Saga publishes TransferFailedEvent{RequiresDebitReversal=false}
  (saga was in Initiated state — debit NEVER happened, no reversal)
      │
      ▼
CompensateDebitConsumer.Consume<TransferFailedEvent>
      └─ RequiresDebitReversal=false → exits immediately (no reversal)
         Source account balance unchanged ✓
```

**Infrastructure requirement:** Saga timeouts use `UseDelayedMessageScheduler()` which relies on the `rabbitmq_delayed_message_exchange` RabbitMQ plugin. The `docker-compose.yml` uses `heidiks/rabbitmq-delayed-message-exchange:3.13.7-management` which ships with the plugin enabled.

```csharp
// TransferWorker/Program.cs
x.UsingRabbitMq((ctx, cfg) =>
{
    cfg.UseDelayedMessageScheduler();  // ← required for Schedule() in state machines
    ...
});
```

---

## 6. Production best practices

### Atomicity — Outbox
Database writes and message publishes are always committed in the same transaction. No message is sent if the DB write fails; no DB write exists without a matching message being delivered eventually.

### Idempotency — Inbox
Every consumer checks whether it has already processed a given `MessageId` before doing any work. Safe to re-deliver any message at any time.

### Audit trail
Every state transition is timestamped:
- `Transfer.CreatedAt` / `CompletedAt` / `FailureReason`
- `InboxMessage.ReceivedAt` / `ProcessedAt`
- `TransferState` tracks every state the saga passed through
- All events carry `OccurredAt`

### Concurrency limits + PrefetchCount = ConcurrencyLimit
```csharp
e.UseConcurrencyLimit(5);
e.PrefetchCount = 5;   // must equal ConcurrencyLimit
```
`PrefetchCount` controls how many messages are buffered in memory before the consumer starts processing them. Setting it higher than `ConcurrencyLimit` means messages sit in-memory unacknowledged while all slots are busy. Under a crash, those buffered messages are re-queued — but they may have been partially processed. **Setting `PrefetchCount == ConcurrencyLimit` ensures exactly the right number of messages are in-flight at any moment**, minimising re-queue blast on restart.

| Queue | ConcurrencyLimit | PrefetchCount |
|---|---|---|
| `transfer-initiate` | 5 | 5 |
| `transfer-debit` | 3 | 3 |
| `transfer-credit` | 3 | 3 |
| `notification-queue` | 10 | 10 |

### Structured correlation
Every log line, span, and event carries both `TransferId` and `CorrelationId`. In Graylog you can search `correlation_id:"xxx"` to see the complete timeline of a single transfer across all three services. The `X-Correlation-Id` HTTP header is automatically set on every API response by `CorrelationMiddleware` (part of `Company.Observability`) so clients can reference it in support tickets.

### PII redaction
`Company.Observability` runs a `RedactionEnricher` before any log event reaches a sink. Fields such as `password`, `token`, `authorization`, `accountNumber`, and `iban` are replaced with `***` automatically — banking-critical for compliance.

### DB-managed `rowversion` for atomic concurrency tokens
`Account.RowVersion` is mapped to a SQL Server `rowversion` column (`.IsRowVersion().IsConcurrencyToken()`). The database increments it atomically on every `UPDATE` — application code never touches it. This eliminates the race condition where two consumers both read `RowVersion=5`, both compute `6`, and the second write succeeds silently. A DB-managed counter can never be read-then-incremented from two concurrent transactions simultaneously.

### Compensating transactions for partial failures
If the credit step fails permanently after the debit step has succeeded, compensation is handled by two focused classes sharing `CompensateDebitService`: `CompensateDebitFaultConsumer` handles `Fault<CreditAccountCommand>` (credit retries exhausted) and `CompensateDebitTimeoutConsumer` handles `TransferFailedEvent{RequiresDebitReversal=true}` (saga credit timeout). Each class implements exactly one `IConsumer<T>` — each endpoint queue binds to exactly one exchange, eliminating double-processing. The Transfer row is locked with `UPDLOCK + ROWLOCK` before the idempotency check (`transfer.Status == TransferStatus.Debited`) to serialise concurrent callers. Balance and status are updated in one atomic `SaveChangesAsync + CommitAsync`. Emits `CRITICAL` if compensation itself fails after 5 attempts.

### Saga timeouts to prevent frozen transfers
Every saga command dispatch is paired with a scheduled timeout (`TransferDebitTimeout` / `TransferCreditTimeout`). If the expected response never arrives, the timeout fires after 5 minutes and transitions the saga to `Failed`. The published `TransferFailedEvent` carries `RequiresDebitReversal` set to the correct value for the saga's state: `true` when in `Debited` (triggers `CompensateDebitTimeoutConsumer`), `false` when in `Initiated` (debit never happened — no action needed). This prevents both frozen transfers and phantom compensations.

### Health check endpoints on all services
All three services expose `/health/live` (lightweight process liveness) and `/health/ready` (SQL Server + EF Core readiness). These are the standard probes consumed by Kubernetes liveness/readiness, App Service health checks, and load balancers.

```
GET /health/live   → 200 OK  (process is alive; no external checks)
GET /health/ready  → 200 OK  (SQL Server reachable, EF Core migrations applied)
                   → 503     (DB unreachable — remove from load balancer rotation)
```

### TransferStatus constants — no magic strings
All `Transfer.Status` assignments use `TransferStatus.Pending / Debited / Completed / Failed` from `BankingMessaging.Infrastructure.Entities.TransferStatus` (a static constants class). Raw string literals are banned — renaming a status value is a compile-time error rather than a silent runtime bug.

### Contract versioning for rolling deployment
New fields added to message contracts must be nullable or carry a safe default (see `ContractVersioning.cs`). This allows producer and consumer services to be updated independently without simultaneous downtime. MassTransit 8.x uses `System.Text.Json` by default, which ignores unknown fields on deserialization — adding a field to a contract is always backward-compatible.

### Quorum queues for durability
All queues — including error/DLQ queues — are declared as Quorum queues (`x-queue-type: quorum`). Messages are Raft-replicated across cluster nodes before the broker ACKs receipt, eliminating data loss on node failure. Classic queues are explicitly avoided for financial workloads.

### Graceful shutdown
Worker services use `IHostedService` lifetime management. On SIGTERM, MassTransit finishes in-flight messages before stopping, preventing mid-processing abandonment.

### No `Thread.Sleep`
All waits use `await Task.Delay(...)` — non-blocking, returns the thread to the pool during the wait.

### Configuration over code
All connection strings, credentials, and queue settings are driven by `appsettings.json` and environment variables. Nothing is hardcoded. Docker Compose sets environment variables, making the same binary run locally and in production.

### Circuit breaker protects database
Without the circuit breaker, a SQL Server outage would cause every message to fail, be retried 5 times each, and fill the DLQ instantly. The breaker trips after the first wave of failures and halts processing until SQL Server recovers, preventing queue saturation.

### Separation of failure domains
| Concern | What happens if it fails |
|---|---|
| SQL Server down | Circuit breaker trips in worker; API returns 5xx; outbox messages queue up safely |
| RabbitMQ down | API can't accept new transfers (no outbox relay); existing transfers that are in-flight remain in DB until broker recovers |
| TransferWorker crash | RabbitMQ holds unacknowledged messages; they are redelivered when worker restarts |
| NotificationWorker crash | `SendNotificationCommand` messages queue up in `notification-queue`; delivered when worker restarts |

---

## 7. Observability stack

The project uses **[Company.Observability](https://github.com/gzviadauri-dev/ZvikTech.Observability)** — a production-ready NuGet package that wires the complete observability pipeline in two lines for the API, and via `IServiceCollection` extensions for the worker services.

### Signal pipeline

```
  ┌──────────────────────────────────────────────────────────────────────┐
  │  Each .NET 8 service                                                  │
  │                                                                       │
  │  Serilog pipeline (Company.Observability):                            │
  │    CorrelationMiddleware  → sets X-Correlation-Id header (API only)   │
  │    TraceEnricher          → adds trace_id / span_id from Activity     │
  │    RedactionEnricher      → masks PII (password, token, iban, ...)    │
  │    SamplingFilter         → drops % of success HTTP logs in prod      │
  │    RateLimitFilter        → caps repeated Warning floods              │
  │    WriteTo.Async ─────────┬──► Console (compact JSON, always on)      │
  │                           └──► Graylog GELF (UDP/TCP, opt-in)         │
  │                                                                       │
  │  OpenTelemetry SDK (Company.Observability):                           │
  │    AspNetCore instrumentation  (API spans)                            │
  │    HttpClient instrumentation  (outbound spans)                       │
  │    MassTransit source          (publish / consume spans)              │
  │    Runtime / process metrics                                          │
  └──────────────────────────────────┬───────────────────────────────────┘
                                     │ OTLP gRPC :4317
                                     ▼
                          ┌──────────────────┐
                          │  Jaeger           │
                          │  :16686 (UI)      │
                          │  Distributed      │
                          │  trace viewer     │
                          └──────────────────┘

  Serilog GELF ──► Graylog :9000 (opt-in, --profile graylog)
```

### API setup (two lines)

```csharp
// Program.cs — before builder.Build()
builder.AddCompanyObservability();   // Serilog + OTel + correlation + PII redaction

var app = builder.Build();

// After builder.Build()
app.UseCompanyObservability();       // CorrelationMiddleware + request logging + /metrics
```

### Worker setup (IServiceCollection extensions)

`AddCompanyObservability` is a `WebApplicationBuilder`-only extension. Worker services use the underlying public extensions directly — the full observability contract is honoured:

```csharp
// ObservabilityOptions bound from "Observability" appsettings section
builder.Services.AddOptions<ObservabilityOptions>()
    .BindConfiguration(ObservabilityOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

// Two-stage Serilog: bootstrap logger captures startup errors,
// DI-aware logger activates the full pipeline after the container is built.
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(new CompactJsonFormatter())
    .CreateBootstrapLogger();

builder.Services.AddSerilog((services, cfg) => { /* reads ObservabilityOptions */ });

// OpenTelemetry (traces + metrics), MassTransit source added on top
builder.Services.AddCompanyTelemetry(builder.Configuration, builder.Environment);
builder.Services.AddOpenTelemetry().WithTracing(t => t.AddSource("MassTransit"));
```

### Configuration (appsettings.json — all three services)

```json
"Observability": {
  "ServiceName": "BankingMessaging.Api",
  "ServiceVersion": "1.0.0",
  "Logging": {
    "MinimumLevel": "Information",
    "ExcludePaths": ["/health", "/metrics"],
    "SampleSuccessRate": 1.0,
    "EnableConsoleOutput": true,
    "Graylog": {
      "Enabled": false,
      "Host": "graylog",
      "Port": 12201,
      "Protocol": "udp"
    },
    "RateLimit": { "Enabled": true, "MaxPerWindow": 100, "WindowSeconds": 60 }
  },
  "Telemetry": {
    "Tracing": { "Enabled": true, "SamplingRatio": 1.0 },
    "Metrics": { "Enabled": true },
    "Otlp": { "Enabled": true, "Endpoint": "http://localhost:4317" }
  },
  "Redaction": { "SensitiveKeys": ["accountNumber", "iban"] }
}
```

### Three signals correlated by trace_id

| Signal | Sink | Key field |
|---|---|---|
| Logs | Console (always) + Graylog (opt-in) | `trace_id`, `correlation_id`, `service.name` |
| Traces | Jaeger via OTLP | trace ID links back to log entries |
| Metrics | Runtime counters (Prometheus opt-in) | `service.name`, `deployment.environment` |

Because `TraceEnricher` stamps every log event with the same W3C `trace_id` that OTel uses for the current span, you can copy a `trace_id` from Graylog and paste it directly into the Jaeger search bar to jump from a log entry to its full distributed trace.

### What Graylog shows you
- Every `InitiateTransfer` request with its `TransferId` and `correlation_id`
- Every retry attempt with attempt number
- Concurrency conflicts and their resolution
- Dead-lettered messages with full exception stacks
- PII-redacted fields replaced with `***`

### What Jaeger shows you
Each HTTP request generates a root span. Every subsequent MassTransit publish/consume creates child spans, producing a full distributed trace like:

```
POST /api/transfers                          [120ms]
  └─ Publish InitiateTransferCommand          [2ms]
       └─ Consume InitiateTransferCommand     [15ms]
            └─ Publish TransferInitiatedEvent [2ms]
                 └─ Saga: Initiated → Debited [5ms]
                      └─ Publish DebitAccountCommand [2ms]
                           └─ Consume DebitAccountCommand [25ms]
                                └─ Publish AccountDebitedEvent [2ms]
                                     └─ Saga: Debited → Completed [5ms]
                                          └─ Publish CreditAccountCommand ...
```

---

## 8. End-to-end request flow

```
┌─────────────────────────────────────────────────────────────────────────────────┐
│  Step 1: Client sends POST /api/transfers                                        │
│                                                                                   │
│  TransferController                                                               │
│    ├─ Creates Transfer row (Status=Pending)                                      │
│    ├─ Calls IPublishEndpoint.Publish(InitiateTransferCommand) → Outbox table    │
│    └─ SaveChangesAsync()  ← single atomic DB transaction                        │
│    Returns: 202 Accepted { transferId, correlationId }                          │
└────────────────────────────────────────────┬────────────────────────────────────┘
                                             │ MassTransit Outbox Relay
                                             ▼
┌─────────────────────────────────────────────────────────────────────────────────┐
│  Step 2: InitiateTransferConsumer                                                │
│                                                                                   │
│    ├─ INSERT InboxMessage (atomic, PK unique constraint is the guard)           │
│    │    → DbUpdateException(2627) on duplicate → skip silently (idempotent)     │
│    ├─ Load fromAccount from DB (IsDeleted = 0 guard)                            │
│    ├─ Validate balance ≥ amount (throws InsufficientFundsException if not)      │
│    ├─ InboxMessage.ProcessedAt = UtcNow → SaveChanges                           │
│    └─ Publish TransferInitiatedEvent                                             │
└────────────────────────────────────────────┬────────────────────────────────────┘
                                             │
                                             ▼
┌─────────────────────────────────────────────────────────────────────────────────┐
│  Step 3: TransferStateMachine receives TransferInitiatedEvent                    │
│                                                                                   │
│    ├─ Creates saga instance (CorrelationId = key)                                │
│    ├─ Stores: FromAccountId, ToAccountId, Amount                                │
│    ├─ State: Initial → Initiated                                                 │
│    ├─ Publishes DebitAccountCommand                                              │
│    └─ Schedules TransferDebitTimeout (5 min watchdog)                           │
└────────────────────────────────────────────┬────────────────────────────────────┘
                                             │
                                             ▼
┌─────────────────────────────────────────────────────────────────────────────────┐
│  Step 4: DebitAccountConsumer                                                    │
│                                                                                   │
│    ├─ BEGIN TRANSACTION (RepeatableRead)                                         │
│    ├─ SELECT * FROM Accounts WITH (UPDLOCK, ROWLOCK)                            │
│    │    WHERE AccountId = @from AND IsDeleted = 0                               │
│    ├─ Validate balance ≥ amount                                                  │
│    ├─ account.Balance -= amount; account.UpdatedAt = UtcNow                     │
│    │    (RowVersion is auto-incremented by SQL Server on UPDATE — never touched) │
│    ├─ SaveChangesAsync()  ← throws DbUpdateConcurrencyException if rowversion   │
│    │    mismatch → retry loop detaches only the Account entity and re-reads     │
│    ├─ COMMIT                                                                     │
│    └─ Publish AccountDebitedEvent                                                │
└────────────────────────────────────────────┬────────────────────────────────────┘
                                             │
                                             ▼
┌─────────────────────────────────────────────────────────────────────────────────┐
│  Step 5: TransferStateMachine receives AccountDebitedEvent                       │
│                                                                                   │
│    ├─ Unschedule(DebitTimeout) — debit arrived in time, cancel watchdog         │
│    ├─ State: Initiated → Debited                                                 │
│    ├─ Publishes CreditAccountCommand                                             │
│    └─ Schedules TransferCreditTimeout (5 min watchdog)                          │
└────────────────────────────────────────────┬────────────────────────────────────┘
                                             │
                                             ▼
┌─────────────────────────────────────────────────────────────────────────────────┐
│  Step 6: CreditAccountConsumer                                                   │
│                                                                                   │
│    ├─ BEGIN TRANSACTION (RepeatableRead)                                         │
│    ├─ SELECT * FROM Accounts WITH (UPDLOCK, ROWLOCK)                            │
│    │    WHERE AccountId = @to AND IsDeleted = 0                                 │
│    ├─ account.Balance += amount; account.UpdatedAt = UtcNow                     │
│    │    (RowVersion auto-incremented by SQL Server on UPDATE)                   │
│    ├─ SaveChangesAsync()                                                         │
│    ├─ COMMIT                                                                     │
│    └─ Publish AccountCreditedEvent                                               │
└────────────────────────────────────────────┬────────────────────────────────────┘
                                             │
                                             ▼
┌─────────────────────────────────────────────────────────────────────────────────┐
│  Step 7: TransferStateMachine receives AccountCreditedEvent                      │
│                                                                                   │
│    ├─ Unschedule(CreditTimeout) — credit arrived in time, cancel watchdog       │
│    ├─ saga.CompletedAt = UtcNow                                                  │
│    ├─ State: Debited → Completed                                                 │
│    ├─ Publish TransferCompletedEvent                                             │
│    └─ Publish SendNotificationCommand                                            │
└────────────────────────────────────────────┬────────────────────────────────────┘
                                             │
                                             ▼
┌─────────────────────────────────────────────────────────────────────────────────┐
│  Step 8: SendNotificationConsumer                                                │
│                                                                                   │
│    └─ Send email / SMS / push to account holder                                  │
└─────────────────────────────────────────────────────────────────────────────────┘
```

---

## 9. Failure scenarios and how they are handled

### Scenario A — Insufficient funds

```
InitiateTransferConsumer
  └─ balance < amount → throws InsufficientFundsException
       │
       ├─ Retry config: r.Ignore<InsufficientFundsException>()
       │  → NO retries (business rule, not transient)
       │
       └─ Fault<InitiateTransferCommand> published to _error queue
            │
            └─ DeadLetterConsumer
                 └─ UPDATE Transfers SET Status='Failed', FailureReason='insufficient funds'
```

### Scenario B — Transient DB error (SQL Server briefly unavailable)

```
DebitAccountConsumer
  └─ SQL Server timeout → throws TransientDatabaseException
       │
       ├─ Retry config: r.Handle<TransientDatabaseException>()
       │  → retry with exponential back-off
       │
       Attempt 1: fail → wait 1s
       Attempt 2: fail → wait 4s
       Attempt 3: SUCCESS → AccountDebitedEvent published
```

### Scenario C — Consumer crash mid-message

```
DebitAccountConsumer
  ├─ Reads message from RabbitMQ (message is now "unacknowledged")
  ├─ Starts processing...
  └─ PROCESS CRASHES

  RabbitMQ: unacknowledged message timeout → message requeued
  Consumer restarts
  └─ Message redelivered
       └─ Inbox check: MessageId not in InboxMessages → process normally
          (RowVersion lock prevents double-debit even if it got through twice)
```

### Scenario D — API crash after DB write, before Outbox relay

```
TransferController
  ├─ INSERT Transfer + INSERT OutboxMessage → COMMITTED ✓
  └─ API CRASHES

  API restarts
  └─ MassTransit Outbox Relay starts
       └─ Finds unsent OutboxMessage row
            └─ Delivers InitiateTransferCommand to RabbitMQ
```

### Scenario E — 5 simultaneous debits on same account (race condition test)

```
5 consumers read ACC-001 (balance=1000) concurrently

With UPDLOCK + RepeatableRead:
  Consumer 1 acquires lock → debit 50 → balance=950 → COMMIT → releases lock
  Consumer 2 was blocked   → acquires lock → reads 950 → debit 50 → balance=900 → COMMIT
  Consumer 3 was blocked   → acquires lock → reads 900 → debit 50 → balance=850 → COMMIT
  Consumer 4 was blocked   → acquires lock → reads 850 → debit 50 → balance=800 → COMMIT
  Consumer 5 was blocked   → acquires lock → reads 800 → debit 50 → balance=750 → COMMIT

Final balance: 750 (correct — exactly 5 × 50 debited, never negative)
```

### Scenario F — Credit fails permanently (compensating transaction)

```
CreditAccountConsumer
  └─ all 3 retries exhausted
        │  MassTransit publishes
        ▼
  Fault<CreditAccountCommand>
        │  routes to transfer-credit-fault-handler queue
        ▼
  CompensateDebitFaultConsumer.Consume<Fault<CreditAccountCommand>>
    └─ CompensateDebitService.ReverseDebit()
         ├─ BEGIN TRANSACTION (RepeatableRead)
         ├─ SELECT * FROM Transfers WITH (UPDLOCK, ROWLOCK)   ← serialises concurrent compensation
         ├─ Guard: transfer.Status == TransferStatus.Debited? ← idempotency
         ├─ SELECT source account WITH (UPDLOCK, ROWLOCK) AND IsDeleted = 0
         ├─ sourceAccount.Balance += transfer.Amount          ← reverse the debit
         ├─ transfer.Status = TransferStatus.Failed
         ├─ SaveChangesAsync()                                ← ONE call (atomic: balance + status)
         └─ COMMIT
    └─ Publish TransferFailedEvent{RequiresDebitReversal=false}
          │  (reversal already done — saga should finalize, not re-compensate)
          ▼
  TransferStateMachine During(Debited) When(TransferFailed)
    ├─ Unschedule(CreditTimeout)
    └─ State: Debited → Failed

  CompensateDebitTimeoutConsumer.Consume<TransferFailedEvent>
    └─ RequiresDebitReversal=false → exits immediately (no double-reversal)
```

**Result:** Source account balance is restored exactly once. Transfer is `Failed` with a clear reason. No money is lost.

**If compensation itself fails after 5 attempts:** `CRITICAL` log emitted — requires manual intervention.

---

### Scenario G — Saga timeout (consumer permanently unavailable)

**Debit timeout — no compensation needed:**

```
TransferStateMachine in Initiated state
  └─ Published DebitAccountCommand + scheduled DebitTimeout (T+5min)

[5 minutes pass — no AccountDebitedEvent arrives]
  DebitTimeout fires
    │
    ▼
  TransferStateMachine During(Initiated)
    ├─ FailureReason = "Debit timed out"
    ├─ State: Initiated → Failed
    └─ Publish TransferFailedEvent{RequiresDebitReversal=false}
               ↓
  CompensateDebitTimeoutConsumer: RequiresDebitReversal=false → skip
  Source account balance: UNCHANGED ✓  (debit never happened)
```

**Credit timeout — compensation required:**

```
TransferStateMachine in Debited state
  └─ Published CreditAccountCommand + scheduled CreditTimeout (T+5min)

[5 minutes pass — no AccountCreditedEvent arrives]
  CreditTimeout fires
    │
    ▼
  TransferStateMachine During(Debited)
    ├─ FailureReason = "Credit timed out"
    ├─ State: Debited → Failed
    └─ Publish TransferFailedEvent{RequiresDebitReversal=true}
               ↓
  CompensateDebitTimeoutConsumer: RequiresDebitReversal=true → proceed
    └─ CompensateDebitService.ReverseDebit()
         ├─ SELECT * FROM Transfers WITH (UPDLOCK, ROWLOCK)
         ├─ Guard: transfer.Status == TransferStatus.Debited
         ├─ Reverses debit (UPDLOCK + ROWLOCK on Account, 5-attempt loop)
         └─ transfer.Status = TransferStatus.Failed
  Source account balance: RESTORED ✓
```

**Result:** No transfer stays frozen. Debit timeout produces no side effects on account balance. Credit timeout guarantees the debit is reversed exactly once.

---

## 10. Technology decisions

| Technology | Version | Why chosen |
|---|---|---|
| **.NET 8** | LTS | Long-term support, latest performance gains, minimal API improvements |
| **MassTransit** | 8.3.5 | De-facto standard for .NET messaging; last fully open-source version; first-class saga + outbox + retry + schedule support |
| **RabbitMQ** | `heidiks/rabbitmq-delayed-message-exchange:3.13.7-management` | Standard management image + `rabbitmq_delayed_message_exchange` plugin pre-installed; required for `UseDelayedMessageScheduler()` saga timeouts |
| **Quorum Queues** | RabbitMQ built-in | Raft-replicated queue type replacing Classic queues; no message loss on node failure; recommended for production finance workloads |
| **SQL Server `rowversion`** | EF Core 9 `.IsRowVersion()` | DB-managed atomic concurrency token; eliminates the read-then-increment race condition present in manually-incremented `long` tokens |
| **SQL Server** | 2022 | Production-grade RDBMS, native support in EF Core 9, excellent row-locking primitives (`UPDLOCK`, `ROWLOCK`) |
| **EF Core** | 9.0.1 | Tight MassTransit outbox/saga integration; migrations; `FromSqlRaw` for lock hints; `AddDbContextCheck<T>` for health checks |
| **ASP.NET Core Health Checks** | .NET 8 built-in | `/health/live` + `/health/ready` on all services; SQL Server + EF Core checks for readiness probes |
| **Company.Observability** | 1.0.0 | Production-ready observability package: Serilog → Graylog (GELF) + OTel traces + metrics (OTLP/Prometheus), PII redaction, correlation IDs, sampling, rate-limit filters — wired in two lines |
| **Jaeger** | all-in-one | Zero-config OTLP-native distributed trace viewer; receives spans directly from all three services |
| **Graylog** | 6.0 (opt-in) | Log aggregation with full-text search; backed by OpenSearch; activated via `--profile graylog` |
| **xUnit** | 2.x | Standard .NET test framework |
| **Testcontainers** | 4.x | Real SQL Server + RabbitMQ in Docker for integration tests — no mocks, no fakes |

### Why MassTransit over raw RabbitMQ client?

| Feature | Raw `RabbitMQ.Client` | MassTransit |
|---|---|---|
| Retry policies | Manual implementation | Built-in, configurable |
| Dead letter routing | Manual exchange setup | Automatic `_error` queues |
| Saga persistence | Build from scratch | EF Core provider included |
| Outbox pattern | Build from scratch | `AddEntityFrameworkOutbox` |
| OpenTelemetry | Manual instrumentation | Auto-instrumented |
| Circuit breaker | Build from scratch | `UseCircuitBreaker` middleware |
| Test harness | No equivalent | `AddMassTransitTestHarness` |

MassTransit eliminates ~1,500 lines of infrastructure plumbing that would otherwise need to be built, tested, and maintained.
