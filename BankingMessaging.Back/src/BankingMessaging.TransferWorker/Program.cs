using BankingMessaging.Infrastructure.Exceptions;
using BankingMessaging.Infrastructure.Persistence;
using BankingMessaging.Infrastructure.Sagas;
using BankingMessaging.TransferWorker.Consumers;
using Company.Observability.Configuration;
using Company.Observability.Telemetry;
using MassTransit;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);

// ── Company.Observability: bind + validate options ────────────────────────────────────
builder.Services
    .AddOptions<ObservabilityOptions>()
    .BindConfiguration(ObservabilityOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

// ── Company.Observability: bootstrap logger (captures startup exceptions) ─────────────
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("System", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(new CompactJsonFormatter())
    .CreateBootstrapLogger();

// ── Company.Observability: full DI-aware Serilog logger ───────────────────────────────
builder.Services.AddSerilog((services, cfg) =>
{
    var opts = services.GetRequiredService<IOptions<ObservabilityOptions>>().Value;

    cfg
        .MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
        .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
        .MinimumLevel.Override("System", LogEventLevel.Warning)
        .Enrich.FromLogContext()
        .Enrich.WithMachineName()
        .Enrich.WithProcessId()
        .Enrich.WithThreadId()
        .Enrich.WithProperty("service.name", opts.ServiceName)
        .Enrich.WithProperty("service.version", opts.ServiceVersion ?? "1.0.0")
        .Enrich.WithProperty("deployment.environment",
            opts.Environment ?? builder.Environment.EnvironmentName)
        .WriteTo.Async(a =>
        {
            a.Console(new CompactJsonFormatter());
        }, bufferSize: 10_000, blockWhenFull: false);
});

// ── Company.Observability: OpenTelemetry (traces + metrics) ──────────────────────────
builder.Services.AddCompanyTelemetry(builder.Configuration, builder.Environment);

builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource("MassTransit"));

// ── Application services ──────────────────────────────────────────────────────────────
var connectionString = builder.Configuration.GetConnectionString("Default")!;

builder.Services.AddDbContext<BankingDbContext>(opt =>
    opt.UseSqlServer(connectionString));

// Shared compensation logic — scoped so each MassTransit message scope gets its own
// instance tied to the same BankingDbContext as the consumer.
builder.Services.AddScoped<CompensateDebitService>();

builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<InitiateTransferConsumer>();
    x.AddConsumer<DebitAccountConsumer>();
    x.AddConsumer<CreditAccountConsumer>();
    x.AddConsumer<DeadLetterConsumer>();
    // Compensation is split across two focused consumers — one per message type —
    // so each endpoint queue binds to exactly one exchange and messages are never
    // delivered to both queues simultaneously (which would cause concurrent double-compensation).
    x.AddConsumer<CompensateDebitFaultConsumer>();
    x.AddConsumer<CompensateDebitTimeoutConsumer>();

    x.AddSagaStateMachine<TransferStateMachine, TransferState>()
        .EntityFrameworkRepository(r =>
        {
            r.ConcurrencyMode = ConcurrencyMode.Optimistic;
            r.UseSqlServer();
            r.ExistingDbContext<BankingDbContext>();
        });

    x.UsingRabbitMq((ctx, cfg) =>
    {
        var rmq = builder.Configuration.GetSection("RabbitMQ");
        cfg.Host(rmq["Host"] ?? "localhost", rmq["VirtualHost"] ?? "/", h =>
        {
            h.Username(rmq["Username"] ?? "guest");
            h.Password(rmq["Password"] ?? "guest");
        });

        // Required for saga Schedule() / Unschedule() — uses RabbitMQ delayed exchange plugin.
        // The docker-compose rabbitmq service uses heidiks/rabbitmq-delayed-message-exchange image.
        cfg.UseDelayedMessageScheduler();

        // FIX 8: MassTransit 8.x System.Text.Json serializer already ignores unknown fields
        // by default — no separate Newtonsoft package is needed. The ContractVersioning strategy
        // (nullable/defaulted new fields) ensures safe rolling deployment without serializer changes.

        cfg.UseCircuitBreaker(cb =>
        {
            cb.TrackingPeriod = TimeSpan.FromMinutes(1);
            cb.TripThreshold  = 15;
            cb.ActiveThreshold = 10;
            cb.ResetInterval  = TimeSpan.FromMinutes(5);
        });

        // FIX 6: PrefetchCount == ConcurrencyLimit — prevents messages sitting unacknowledged
        // in memory while the consumer is at capacity. A crashed process re-queues exactly
        // the number of messages it was actively processing, not silent overflow.
        cfg.ReceiveEndpoint("transfer-initiate", e =>
        {
            e.SetQueueArgument("x-queue-type", "quorum");
            e.UseMessageRetry(r =>
            {
                r.Exponential(5, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(5));
                r.Ignore<InsufficientFundsException>();
                r.Ignore<AccountNotFoundException>();
                r.Handle<TransientDatabaseException>();
            });
            e.UseConcurrencyLimit(5);
            e.PrefetchCount = 5;   // equals ConcurrencyLimit
            e.ConfigureConsumer<InitiateTransferConsumer>(ctx);
        });

        cfg.ReceiveEndpoint("transfer-debit", e =>
        {
            e.SetQueueArgument("x-queue-type", "quorum");
            e.UseMessageRetry(r =>
            {
                r.Exponential(3, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(3));
                r.Ignore<InsufficientFundsException>();
                r.Ignore<AccountNotFoundException>();
                r.Handle<TransientDatabaseException>();
            });
            e.UseConcurrencyLimit(3);
            e.PrefetchCount = 3;   // equals ConcurrencyLimit
            e.ConfigureConsumer<DebitAccountConsumer>(ctx);
        });

        cfg.ReceiveEndpoint("transfer-credit", e =>
        {
            e.SetQueueArgument("x-queue-type", "quorum");
            e.UseMessageRetry(r =>
            {
                r.Exponential(3, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(3));
                r.Ignore<AccountNotFoundException>();
                r.Handle<TransientDatabaseException>();
            });
            e.UseConcurrencyLimit(3);
            e.PrefetchCount = 3;   // equals ConcurrencyLimit
            e.ConfigureConsumer<CreditAccountConsumer>(ctx);
        });

        cfg.ReceiveEndpoint("transfer-fault-handler", e =>
        {
            e.SetQueueArgument("x-queue-type", "quorum");
            e.ConfigureConsumer<DeadLetterConsumer>(ctx);
        });

        // Compensation path 1: Fault<CreditAccountCommand> — credit consumer exhausted broker retries.
        // CompensateDebitFaultConsumer implements only IConsumer<Fault<CreditAccountCommand>>,
        // so this queue binds to exactly ONE exchange. No broker-level retry — the shared
        // CompensateDebitService has its own 5-attempt internal loop; broker retry would risk
        // double-compensation before the previous attempt's transaction commits.
        cfg.ReceiveEndpoint("transfer-credit-fault-handler", e =>
        {
            e.SetQueueArgument("x-queue-type", "quorum");
            e.UseMessageRetry(r => r.None());
            e.PrefetchCount = 1;        // compensation is a critical path — one at a time
            e.UseConcurrencyLimit(1);
            e.ConfigureConsumer<CompensateDebitFaultConsumer>(ctx);
        });

        // Compensation path 2: TransferFailedEvent{RequiresDebitReversal=true} —
        // saga credit timeout fired while saga was in the Debited state.
        // CompensateDebitTimeoutConsumer implements only IConsumer<TransferFailedEvent>,
        // so this queue binds to exactly ONE exchange — no cross-binding with path 1.
        cfg.ReceiveEndpoint("transfer-failed-compensation-handler", e =>
        {
            e.SetQueueArgument("x-queue-type", "quorum");
            e.UseMessageRetry(r => r.None());
            e.PrefetchCount = 1;
            e.UseConcurrencyLimit(1);
            e.ConfigureConsumer<CompensateDebitTimeoutConsumer>(ctx);
        });

        // Saga endpoint — explicit so quorum can be applied
        cfg.ReceiveEndpoint("TransferState", e =>
        {
            e.SetQueueArgument("x-queue-type", "quorum");
            e.ConfigureSaga<TransferState>(ctx);
        });
    });
});

// ── Health checks ─────────────────────────────────────────────────────────────────────
builder.Services.AddHealthChecks()
    .AddSqlServer(
        connectionString,
        name: "sqlserver",
        tags: ["ready"])
    .AddCheck("masstransit-bus", () => HealthCheckResult.Healthy("Bus started"), tags: ["ready"]);

var app = builder.Build();

// Liveness: process is alive (no external checks — just HTTP 200)
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false
});

// Readiness: DB + bus are reachable
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = c => c.Tags.Contains("ready")
});

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<BankingDbContext>();
    await db.Database.MigrateAsync();
}

await app.RunAsync();
