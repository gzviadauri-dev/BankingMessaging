using BankingMessaging.NotificationWorker.Consumers;
using Company.Observability.Configuration;
using Company.Observability.Telemetry;
using MassTransit;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
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
builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<SendNotificationConsumer>();

    x.UsingRabbitMq((ctx, cfg) =>
    {
        var rmq = builder.Configuration.GetSection("RabbitMQ");
        cfg.Host(rmq["Host"] ?? "localhost", rmq["VirtualHost"] ?? "/", h =>
        {
            h.Username(rmq["Username"] ?? "guest");
            h.Password(rmq["Password"] ?? "guest");
        });

        // FIX 8: MassTransit 8.x STJ serializer ignores unknown fields — see ContractVersioning.cs

        // FIX 6: PrefetchCount == ConcurrencyLimit
        cfg.ReceiveEndpoint("notification-queue", e =>
        {
            e.SetQueueArgument("x-queue-type", "quorum");
            e.UseMessageRetry(r => r.Intervals(
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(15),
                TimeSpan.FromSeconds(60)
            ));
            e.UseConcurrencyLimit(10);
            e.PrefetchCount = 10;   // equals ConcurrencyLimit
            e.ConfigureConsumer<SendNotificationConsumer>(ctx);
        });
    });
});

// ── Health checks ─────────────────────────────────────────────────────────────────────
// NotificationWorker has no DB dependency (by design — it does not reference Infrastructure).
// The MassTransit bus is the only external dependency; it reconnects automatically, so
// a static check is registered to give the endpoint the same consistent shape as other services.
// Replace with a real RabbitMQ connectivity check when operator-managed infra monitoring
// is required (add AspNetCore.HealthChecks.Rabbitmq package).
builder.Services.AddHealthChecks()
    .AddCheck("masstransit-bus", () => HealthCheckResult.Healthy("Bus connected"), tags: ["ready"]);

var app = builder.Build();

// Liveness: process is alive (no external checks)
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false
});

// Readiness: bus reachable (tag-filtered — consistent with TransferWorker and Api)
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = c => c.Tags.Contains("ready")
});

await app.RunAsync();
