# BankingMessaging — MassTransit + RabbitMQ Banking Sample

A production-ready .NET 8 solution demonstrating **MassTransit** with **RabbitMQ** in a banking context, covering all critical messaging patterns. Includes a React dashboard SPA for live visualization.

---

## Repository Structure

```
BankingMessaging/
├── BankingMessaging.Back/          ← .NET 8 backend solution
│   ├── src/
│   │   ├── BankingMessaging.Api/              REST API + Transactional Outbox + Health checks
│   │   ├── BankingMessaging.Contracts/        Shared Commands, Events & Timeout messages
│   │   │   └── Versioning/ContractVersioning.cs  ← Backward-compat contract rules
│   │   ├── BankingMessaging.Infrastructure/   EF Core, Saga + Timeouts, Exceptions
│   │   ├── BankingMessaging.TransferWorker/   Consumers + Saga + Compensation
│   │   │   ├── Consumers/CompensateDebitService.cs      ← Shared reversal logic (scoped DI)
│   │   │   ├── Consumers/CompensateDebitFaultConsumer.cs  ← Path 1: Fault<CreditAccountCommand>
│   │   │   ├── Consumers/CompensateDebitTimeoutConsumer.cs ← Path 2: TransferFailedEvent{RequiresDebitReversal=true}
│   │   │   └── Entities/TransferStatus.cs               ← Typed constants for all Transfer.Status values
│   │   └── BankingMessaging.NotificationWorker/
│   ├── tests/
│   │   └── BankingMessaging.IntegrationTests/
│   ├── BankingMessaging.slnx
│   ├── nuget.config                           GitHub Packages feed (Company.Observability)
│   └── Makefile
│
├── BankingMessaging.Front/         ← React 18 + TypeScript SPA
│   └── src/  …                    TanStack Query · Recharts · Tailwind · Sonner
│
├── docker-compose.yml              Infrastructure: RabbitMQ (delayed-exchange) · SQL Server · Jaeger · Graylog
├── OVERVIEW.md                     Architecture deep-dive with diagrams
└── README.md
```

---

## Architecture Overview

```
┌──────────────────────┐    ┌────────────────┐    ┌──────────────────────┐
│  BankingMessaging.   │───▶│   RabbitMQ     │───▶│  TransferWorker      │
│  Api (Publisher)     │    │                │    │  (Consumers + Saga)  │
│  + Swagger UI        │    │  Exchanges &   │◀───│                      │
└──────────────────────┘    │  Quorum Queues │    └──────────────────────┘
         ▲                  │                │           │
         │                  │                │    ┌──────────────────────┐
┌────────┴──────┐           │                │───▶│  NotificationWorker  │
│  React SPA    │           └────────────────┘    └──────────────────────┘
│  :5173        │                  │
└───────────────┘           ┌──────▼──────┐
                            │  SQL Server  │
                            │  + EF Core  │
                            └─────────────┘
```

### Projects

| Project | Role |
|---|---|
| `BankingMessaging.Contracts` | Commands, Events, Timeout messages, Versioning strategy |
| `BankingMessaging.Infrastructure` | EF Core entities (rowversion), DbContext, Saga + Timeouts, Exceptions |
| `BankingMessaging.Api` | REST API publisher with Transactional Outbox + `/health/live` + `/health/ready` |
| `BankingMessaging.TransferWorker` | Consumers: InitiateTransfer, DebitAccount, CreditAccount, CompensateDebitFault (credit exhausted), CompensateDebitTimeout (saga credit timeout), DLQ + health endpoints |
| `BankingMessaging.NotificationWorker` | SendNotification consumer + health endpoints |
| `BankingMessaging.IntegrationTests` | xUnit + Testcontainers integration tests |

---

## Prerequisites

| Tool | Version | Notes |
|---|---|---|
| [.NET 8 SDK](https://dotnet.microsoft.com/download) | 8.0+ | Backend |
| [Docker Desktop](https://www.docker.com/products/docker-desktop/) | Latest | RabbitMQ, SQL Server, Jaeger |
| [Node.js](https://nodejs.org/) | 20+ | React frontend |

> Docker must run **Linux containers** and be able to pull `mcr.microsoft.com/mssql/server:2022-latest` (~1.5 GB on first run).
>
> The RabbitMQ image used is `heidiks/rabbitmq-delayed-message-exchange:3.13.7-management` — a standard RabbitMQ management image with the `rabbitmq_delayed_message_exchange` plugin pre-installed. This plugin is required for saga timeout scheduling (`UseDelayedMessageScheduler`).

---

## Local Development — Step by Step

### Step 1 — Set the NuGet token

`Company.Observability` is hosted on a private GitHub Packages feed.  
Set this environment variable **before** running any `dotnet` command:

**PowerShell:**
```powershell
$env:GITHUB_PACKAGES_TOKEN = "YOUR_GITHUB_PAT_HERE"
```

**Command Prompt / bash:**
```bash
set GITHUB_PACKAGES_TOKEN=YOUR_GITHUB_PAT_HERE   # cmd
export GITHUB_PACKAGES_TOKEN=YOUR_GITHUB_PAT_HERE # bash
```

> Generate a PAT at **GitHub → Settings → Developer settings → Personal access tokens** with the `read:packages` scope.

> To avoid setting it every session, add it to your system environment variables permanently.

---

### Step 2 — Start infrastructure (Docker)

From the **repo root** (`BankingMessaging/`):

```bash
docker-compose up -d
```

Wait ~20 seconds for SQL Server to finish its health check:

```bash
docker-compose ps   # all should show "healthy"
```

| Container | What it provides |
|---|---|
| `banking-rabbitmq` | Message broker — port `5672` |
| `banking-sqlserver` | SQL Server 2022 — port `1433` |
| `banking-jaeger` | Distributed tracing — port `16686` |

---

### Step 3 — Restore & build the backend

```bash
cd BankingMessaging.Back
dotnet restore
dotnet build
```

> `dotnet restore` uses `nuget.config` which reads `GITHUB_PACKAGES_TOKEN` automatically.

---

### Step 4 — Start all backend services

Open **3 separate terminals**, all from `BankingMessaging.Back/`.

**Terminal 1 — REST API**
```bash
dotnet run --project src/BankingMessaging.Api
```
- Runs DB migrations automatically on first start
- API: http://localhost:5000
- Swagger: http://localhost:5000/swagger

**Terminal 2 — Transfer Worker**
```bash
dotnet run --project src/BankingMessaging.TransferWorker
```
- Consumes `InitiateTransfer`, `DebitAccount`, `CreditAccount`, `CompensateDebit`, DLQ messages
- Runs DB migrations on startup
- Health: http://localhost:5001/health/live and http://localhost:5001/health/ready

**Terminal 3 — Notification Worker**
```bash
dotnet run --project src/BankingMessaging.NotificationWorker
```
- Consumes `SendNotification` commands
- Health: http://localhost:5002/health/live

Wait until each terminal shows `MassTransit started` before proceeding.

---

### Step 5 — Start the React frontend

```bash
cd BankingMessaging.Front
npm install
npm run dev
```

- Dashboard: **http://localhost:5173**
- Vite proxies all `/api/*` calls to `http://localhost:5000` automatically

---

### All services at a glance

| Service | URL | Credentials |
|---|---|---|
| React Dashboard | http://localhost:5173 | — |
| REST API + Swagger | http://localhost:5000/swagger | — |
| API Health (live) | http://localhost:5000/health/live | — |
| API Health (ready) | http://localhost:5000/health/ready | — |
| RabbitMQ Management | http://localhost:15672 | guest / guest |
| SQL Server | `localhost,1433` | SA / Banking123! |
| Jaeger Traces | http://localhost:16686 | — |

---

### Stop everything

```bash
# From repo root — stops containers, keeps data volumes
docker-compose down

# Stops containers AND wipes all data (fresh start)
docker-compose down -v
```

---

### Optional: Graylog log aggregation

```bash
docker-compose --profile graylog up -d
```

Then set `"Graylog": { "Enabled": true }` in any `appsettings.json` and restart that service.  
Graylog UI: http://localhost:9000 (admin / admin)

---

## Testing Scenarios

### Happy Path

```bash
curl -X POST http://localhost:5000/api/transfers \
  -H "Content-Type: application/json" \
  -d '{"fromAccountId":"ACC-001","toAccountId":"ACC-002","amount":500,"currency":"USD"}'
```

**Expected flow:**
1. API saves Transfer (Pending) + publishes `InitiateTransferCommand` to outbox atomically
2. Outbox delivers message to RabbitMQ after commit
3. `InitiateTransferConsumer` validates, saves to InboxMessages (idempotency), publishes `TransferInitiatedEvent`
4. `TransferStateMachine` receives `TransferInitiatedEvent` → publishes `DebitAccountCommand`
5. `DebitAccountConsumer` debits ACC-001, updates Transfer → **Debited**, publishes `AccountDebitedEvent`
6. Saga receives `AccountDebitedEvent` → publishes `CreditAccountCommand`
7. `CreditAccountConsumer` credits ACC-002, updates Transfer → **Completed**, publishes `AccountCreditedEvent`
8. Saga reaches **Completed** state, publishes `TransferCompletedEvent` + `SendNotificationCommand`
9. `SendNotificationConsumer` sends notification

**Verify:**
```bash
# Check transfer status
curl http://localhost:5000/api/transfers/{transferId}

# Check account balances
curl http://localhost:5000/api/accounts/ACC-001
curl http://localhost:5000/api/accounts/ACC-002
```

---

### Insufficient Funds (Business Exception — No Retry)

```bash
curl -X POST http://localhost:5000/api/transfers \
  -H "Content-Type: application/json" \
  -d '{"fromAccountId":"ACC-001","toAccountId":"ACC-002","amount":99999}'
```

**Expected:**
- `InsufficientFundsException` is thrown
- MassTransit **does NOT retry** (configured via `r.Ignore<InsufficientFundsException>()`)
- `Fault<InitiateTransferCommand>` is published
- `DeadLetterConsumer` marks transfer status as `Failed`

---

### Simulated Transient Error (Exponential Retry)

Send a transfer with `simulateError: true` — or use the **Transient Error** button in the React dashboard:

```bash
curl -X POST http://localhost:5000/api/transfers \
  -H "Content-Type: application/json" \
  -d '{"fromAccountId":"ACC-001","toAccountId":"ACC-002","amount":1,"simulateError":true}'
```

**Expected:**
- `InitiateTransferConsumer` throws `TransientDatabaseException`
- MassTransit retries with exponential backoff
- After exhausting retries → `Fault<InitiateTransferCommand>` → `DeadLetterConsumer` → Transfer status = **Failed**

Observe retry spans in **Jaeger** at `http://localhost:16686`.

---

### Race Condition Prevention

Use `Promise.all` (React dashboard **Race Condition ×3** button) or simultaneous curl calls:

```csharp
var tasks = Enumerable.Range(0, 5).Select(_ =>
    httpClient.PostAsJsonAsync("/api/transfers", new
    {
        fromAccountId = "ACC-001",
        toAccountId = "ACC-002",
        amount = 200
    })
);
await Task.WhenAll(tasks);
```

**Expected:**
- Only valid debits proceed (up to `10000 / 200 = 50`)
- `SELECT … WITH (UPDLOCK, ROWLOCK)` prevents double-spend
- No negative balance
- `DbUpdateConcurrencyException` retries handled internally

---

### Dead Letter Queue (DLQ)

Temporarily set `retryLimit: 0` in `TransferWorker/Program.cs`:

```csharp
e.UseMessageRetry(r => r.None());
```

Then send a transfer that causes an exception.

**Expected:**
- Message immediately goes to `transfer-initiate_error` queue (also a **Quorum** queue)
- Visible in RabbitMQ Management UI: `http://localhost:15672` (guest/guest) — look for `Type: quorum` column
- `DeadLetterConsumer` processes the fault and marks transfer as Failed

---

### Idempotency (Inbox Pattern — Atomic Insert-and-Catch)

If a consumer redelivers the same message (simulate via RabbitMQ management → "Requeue"), the atomic insert into `InboxMessages` hits the PK unique constraint and the consumer exits silently before doing any business logic.

**Expected:** Second processing is a no-op — no duplicate debit, no TOCTOU race between concurrent redeliveries.

---

### Saga Timeout (Transfer Frozen Prevention)

Temporarily kill the TransferWorker after the API publishes a transfer (before `AccountDebitedEvent` arrives).

**Expected:**
- Saga scheduled a `TransferDebitTimeout` when it published `DebitAccountCommand`
- After 5 minutes with no `AccountDebitedEvent`, the saga transitions to `Failed`
- `TransferFailedEvent{RequiresDebitReversal=false}` is published — debit never happened, no compensation
- Source account balance is **unchanged**
- No transfer stays in `Initiated` state forever

---

### Compensating Transaction (Credit Failure → Debit Reversal)

Compensation is handled by two **focused consumer classes** sharing `CompensateDebitService`:

**Test A — Credit fault (retries exhausted):**
1. Temporarily add `throw new Exception("Simulated credit failure");` at the top of `CreditAccountConsumer.Consume`
2. POST a transfer
3. Wait for retries to exhaust (3 attempts × exponential back-off)
4. `Fault<CreditAccountCommand>` is published → `transfer-credit-fault-handler` queue
5. Verify in the database:
   - `Transfer.Status = 'Failed'`
   - `Transfer.FailureReason` contains `"debit reversed"`
   - `Accounts` where `AccountId = fromAccountId` has its original balance **restored**
6. Remove the `throw` line

**Test B — Credit timeout (saga watchdog fires):**
1. Temporarily set `s.Delay = TimeSpan.FromSeconds(10)` on `CreditTimeout` in `TransferStateMachine`
2. Comment out `AddConsumer<CreditAccountConsumer>()` in `Program.cs`
3. POST a transfer — the debit will succeed but credit will never arrive
4. After 10 seconds, saga publishes `TransferFailedEvent{RequiresDebitReversal=true}` → `transfer-failed-compensation-handler` queue
5. Verify in the database: source account balance restored, `Transfer.Status = 'Failed'`
6. Restore the delay and re-enable the consumer

**Test C — Debit timeout (no phantom compensation):**
1. Comment out `AddConsumer<DebitAccountConsumer>()` in `Program.cs`
2. Temporarily set `s.Delay = TimeSpan.FromSeconds(10)` on `DebitTimeout`
3. POST a transfer — debit never happens
4. After 10 seconds, saga publishes `TransferFailedEvent{RequiresDebitReversal=false}`
5. Verify: source account balance is **unchanged** — no phantom reversal occurred
6. Restore everything

---

## Observability

| Tool | URL | Purpose |
|---|---|---|
| React Dashboard | http://localhost:5173 | Live transfer visualization |
| Swagger UI | http://localhost:5000/swagger | API testing |
| RabbitMQ Management | http://localhost:15672 | Queue monitoring, DLQ inspection |
| SQL Server | localhost,1433 (SA / Banking123!) | Database (SSMS / Azure Data Studio) |
| Jaeger (traces) | http://localhost:16686 | Distributed trace visualization |
| Graylog (logs) | http://localhost:9000 (admin/admin) | Centralized structured logs (`--profile graylog`) |

**Start with Graylog:**
```bash
docker-compose --profile graylog up -d
```
Then set `Observability:Logging:Graylog:Enabled=true` in `appsettings.json`.

---

## Key Patterns Demonstrated

| Pattern | Implementation |
|---|---|
| **Transactional Outbox** | `AddEntityFrameworkOutbox<BankingDbContext>` — messages only reach RabbitMQ after DB commit |
| **Idempotent Consumer** | `InboxMessages` table — atomic insert-and-catch on PK (no TOCTOU race) |
| **Saga Orchestration** | `TransferStateMachine` — manages multi-step transfer workflow |
| **Saga Timeouts** | `Schedule<TransferDebitTimeout/CreditTimeout>` + `UseDelayedMessageScheduler()` — transfers can't freeze forever |
| **Compensating Transaction** | Split into `CompensateDebitFaultConsumer` (Path 1: `Fault<CreditAccountCommand>`) and `CompensateDebitTimeoutConsumer` (Path 2: `TransferFailedEvent{RequiresDebitReversal=true}`), sharing `CompensateDebitService`. Each consumer implements exactly one `IConsumer<T>` — no double-binding, no concurrent double-reversal. |
| **DB rowversion Concurrency** | `Account.RowVersion` is a SQL Server `rowversion` (DB-managed binary(8)) + `SELECT … WITH (UPDLOCK, ROWLOCK)` on Account **and Transfer** rows |
| **TransferStatus constants** | `TransferStatus.Pending/Debited/Completed/Failed` typed constants replace raw string literals across all consumers and controllers — rename compiles loudly |
| **Exponential Retry** | `r.Exponential(5, 2s, 60s, 5s)` per receive endpoint |
| **Dead Letter Queue** | `Fault<T>` consumers + `DeadLetterConsumer` |
| **Circuit Breaker** | `UseCircuitBreaker` — trips at 15% failure rate |
| **PrefetchCount = ConcurrencyLimit** | Eliminates in-memory message overflow; limits re-queue blast on crash |
| **Health Checks** | `/health/live` + `/health/ready` on all services; tag-filtered (`"ready"` tag) for consistency across Api, TransferWorker, NotificationWorker |
| **Quorum Queues** | `e.SetQueueArgument("x-queue-type", "quorum")` — Raft-replicated queues on all endpoints |
| **Contract Versioning** | `ContractVersioning.cs` using `System.Text.Json` (MassTransit 8 default) — unknown fields ignored on deserialization; safe rolling deployment when fields are added |
| **Distributed Tracing** | OpenTelemetry → Jaeger via OTLP (Company.Observability) |
| **Structured Logging** | Serilog → Console / Graylog (Company.Observability) |
| **PII Redaction** | Sensitive keys auto-masked via Company.Observability |

---

## Running Tests

```bash
cd BankingMessaging.Back
dotnet test tests/BankingMessaging.IntegrationTests
```

Tests use **Testcontainers** — Docker is required. No manual setup needed.

---

## Using the Makefile

All `make` targets are available from `BankingMessaging.Back/`:

```bash
cd BankingMessaging.Back
make infra           # docker-compose up -d
make migrate         # EF Core migrations
make api             # start API
make worker-transfer # start TransferWorker
make worker-notify   # start NotificationWorker
make test            # run integration tests
make build           # dotnet build
make clean           # dotnet clean
```

---

## Message Flow Diagram

### Happy path

```
InitiateTransferCommand
        │
        ▼
InitiateTransferConsumer
  ✓ Atomic inbox insert-and-catch (no TOCTOU)
  ✓ Funds check
        │ publishes
        ▼
TransferInitiatedEvent
        │
        ▼
TransferStateMachine (Saga)
  State: Initial → Initiated
        │ publishes
        ├──▶ DebitAccountCommand
        └──▶ [schedules DebitTimeout: 5 min]
                    │
                    ▼
            DebitAccountConsumer
              ✓ SELECT FOR UPDATE (UPDLOCK, ROWLOCK)
              ✓ DB-managed rowversion concurrency
              ✓ Per-entity ChangeTracker detach on retry
              ✓ Transfer.Status → Debited
                    │ publishes
                    ▼
            AccountDebitedEvent
                    │
                    ▼
TransferStateMachine (Saga)
  State: Initiated → Debited
  [cancels DebitTimeout]
        │ publishes
        ├──▶ CreditAccountCommand
        └──▶ [schedules CreditTimeout: 5 min]
                    │
                    ▼
            CreditAccountConsumer
              ✓ SELECT FOR UPDATE (UPDLOCK, ROWLOCK)
              ✓ Transfer.Status → Completed
                    │ publishes
                    ▼
            AccountCreditedEvent
                    │
                    ▼
TransferStateMachine (Saga)
  State: Debited → Completed
  [cancels CreditTimeout]
        │ publishes
        ├──▶ TransferCompletedEvent
        └──▶ SendNotificationCommand
                    │
                    ▼
          SendNotificationConsumer
            ✓ Email/SMS/Push sent
```

### Failure path — credit fails permanently (Fault path)

```
CreditAccountConsumer
  └─ all 3 retries exhausted
        │ MassTransit publishes
        ▼
Fault<CreditAccountCommand>
        │  routes to transfer-credit-fault-handler
        ▼
CompensateDebitConsumer
  ✓ transfer.Status == "Debited" guard (idempotent)
  ✓ SELECT source account WITH (UPDLOCK, ROWLOCK)
  ✓ sourceAccount.Balance += amount   ← reverse debit
  ✓ transfer.Status = "Failed"        ← one SaveChanges (atomic)
  ✓ Publishes TransferFailedEvent{RequiresDebitReversal=false}
        │
        ▼
TransferStateMachine (Saga)
  During(Debited) When(TransferFailed)
  [Unschedule CreditTimeout]
  State: Debited → Failed
```

### Failure path — saga timeout (consumer never responds)

**Debit timeout** (saga in Initiated — debit never happened):
```
DebitTimeout fires (5 min, no AccountDebitedEvent)
        │
        ▼
TransferStateMachine During(Initiated)
  └─ Publishes TransferFailedEvent{RequiresDebitReversal=false}
  └─ State: Initiated → Failed

CompensateDebitConsumer receives TransferFailedEvent
  └─ RequiresDebitReversal=false → skips immediately (no phantom reversal)
```

**Credit timeout** (saga in Debited — debit already happened):
```
CreditTimeout fires (5 min, no AccountCreditedEvent)
        │
        ▼
TransferStateMachine During(Debited)
  └─ Publishes TransferFailedEvent{RequiresDebitReversal=true}
  └─ State: Debited → Failed

        │  routes to transfer-failed-compensation-handler
        ▼
CompensateDebitConsumer
  ✓ RequiresDebitReversal=true → proceeds
  ✓ transfer.Status == "Debited" guard (idempotent)
  ✓ Reverses debit on source account (5-attempt retry)
  ✓ transfer.Status = "Failed" (atomic SaveChanges)
```
