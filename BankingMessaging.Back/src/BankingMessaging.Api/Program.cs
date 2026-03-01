using BankingMessaging.Infrastructure.Persistence;
using Company.Observability;
using MassTransit;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Two lines wire everything: Serilog → Graylog/Console, OTel → Jaeger, correlation IDs,
// PII redaction, sampling, rate-limit filters — driven by "Observability" appsettings section.
builder.AddCompanyObservability();

builder.Services.AddDbContext<BankingDbContext>(opt =>
    opt.UseSqlServer(builder.Configuration.GetConnectionString("Default")));

builder.Services.AddMassTransit(x =>
{
    x.AddEntityFrameworkOutbox<BankingDbContext>(o =>
    {
        o.UseSqlServer();
        o.UseBusOutbox();
    });

    x.UsingRabbitMq((ctx, cfg) =>
    {
        var rmq = builder.Configuration.GetSection("RabbitMQ");
        cfg.Host(rmq["Host"] ?? "localhost", rmq["VirtualHost"] ?? "/", h =>
        {
            h.Username(rmq["Username"] ?? "guest");
            h.Password(rmq["Password"] ?? "guest");
        });

        // FIX 8: MassTransit 8.x STJ serializer ignores unknown fields — see ContractVersioning.cs

        cfg.ConfigureEndpoints(ctx);
    });
});

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins("http://localhost:5173")
     .AllowAnyHeader()
     .AllowAnyMethod()));

// ── Health checks ─────────────────────────────────────────────────────────────────────
builder.Services.AddHealthChecks()
    .AddSqlServer(
        builder.Configuration.GetConnectionString("Default")!,
        name: "sqlserver",
        tags: ["ready"])
    .AddDbContextCheck<BankingDbContext>(
        name: "ef-migrations",
        tags: ["ready"]);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "Banking Messaging API", Version = "v1" });
});

var app = builder.Build();

// Adds CorrelationMiddleware (first), Serilog request logging, and /metrics if Prometheus enabled.
app.UseCompanyObservability();
app.UseCors();

// Simple liveness — just proves the process is alive
app.MapGet("/api/health", () => Results.Ok(new { status = "healthy", timestamp = DateTimeOffset.UtcNow }));

// Liveness: is the process alive? (no DB/broker check)
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });

// Readiness: is the process ready to serve traffic? (DB + EF Core migration check)
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = c => c.Tags.Contains("ready")
});

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Run migrations unconditionally so every environment (dev, staging, production) always
// starts with an up-to-date schema. Guard removed — previously this meant migrations never
// ran in non-Development environments and the API would start with a stale schema.
// TransferWorker also runs migrations on startup; whichever starts first wins; the second
// call is a no-op (EF Core skips already-applied migrations).
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<BankingDbContext>();
    await db.Database.MigrateAsync();
}

app.MapControllers();
app.Run();

public partial class Program { }
