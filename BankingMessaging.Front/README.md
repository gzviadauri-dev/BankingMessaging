# BankingMessaging.Front

React SPA for demoing the MassTransit + RabbitMQ banking pipeline.

## Prerequisites

- **BankingMessaging.Api** running on `http://localhost:5000`
- **Docker services** running: `docker-compose up -d` (RabbitMQ + SQL Server + Jaeger)

## Run

```bash
cd BankingMessaging.Front
npm install
npm run dev
# → http://localhost:5173
```

## Features

| Feature | Description |
|---|---|
| **Real-time transfer initiation** | Submit transfers via the form; optimistic `Pending` row appears immediately |
| **Live balance updates** | Account cards poll every 5s to reflect debit/credit changes |
| **Transfer history** | Polls every 3s — watch status progress: `Pending → Debited → Completed` |
| **Activity feed** | Derives events from transfer state; slides in new items every 2s |
| **Chaos controls** | Three chaos buttons in the sidebar to demo failure scenarios |
| **Click-to-fill** | Click an account card to pre-fill it as the "From" in the transfer form |
| **Copy to clipboard** | Transfer IDs and correlation IDs have a copy button |
| **Responsive layout** | 3-column on ≥1024px, single column on smaller screens |

## Chaos Controls

| Button | What it does | What to observe |
|---|---|---|
| **Insufficient Funds** | Sends `$999,999` transfer | Fails with `InsufficientFundsException`, appears in DLQ, status → `Failed` |
| **Transient Error** | Sends with `simulateError: true` | `InitiateTransferConsumer` throws; MassTransit retries. Check Jaeger for retry spans |
| **Race Condition ×3** | Fires 3 concurrent transfers from same account | SQL Server `UPDLOCK` + optimistic concurrency prevents double-spend |

## Observability

- **Jaeger UI**: [http://localhost:16686](http://localhost:16686) — distributed traces per transfer
- **RabbitMQ UI**: [http://localhost:15672](http://localhost:15672) — queue depths and message rates
- **Swagger UI**: [http://localhost:5000/swagger](http://localhost:5000/swagger) — API explorer

## Tech Stack

- React 18 + TypeScript + Vite
- TailwindCSS with custom dark financial theme
- TanStack Query v5 (polling, mutations, cache invalidation)
- Recharts (account balance sparklines)
- Sonner (toast notifications)
- Lucide React (icons)
- date-fns (relative timestamps)
