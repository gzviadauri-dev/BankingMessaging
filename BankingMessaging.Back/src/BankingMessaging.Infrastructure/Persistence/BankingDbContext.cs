using BankingMessaging.Infrastructure.Entities;
using BankingMessaging.Infrastructure.Sagas;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace BankingMessaging.Infrastructure.Persistence;

public class BankingDbContext : DbContext
{
    public BankingDbContext(DbContextOptions<BankingDbContext> options) : base(options) { }

    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Transfer> Transfers => Set<Transfer>();
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<TransferState> TransferStates => Set<TransferState>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<Account>(e =>
        {
            e.HasKey(a => a.AccountId);
            e.Property(a => a.Balance).HasColumnType("numeric(18,4)");

            // SQL Server rowversion: auto-incremented by DB on every UPDATE.
            // IsRowVersion() maps to SQL Server 'rowversion' type (binary(8), DB-generated).
            // IsConcurrencyToken() makes EF Core include it in UPDATE/DELETE WHERE clauses.
            // rowversion columns CANNOT be seeded — remove from HasData below.
            e.Property(a => a.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken();

            e.Property(a => a.IsDeleted).HasDefaultValue(false);
        });

        b.Entity<Transfer>(e =>
        {
            e.HasKey(t => t.TransferId);
            e.Property(t => t.Amount).HasColumnType("numeric(18,4)");
        });

        b.Entity<InboxMessage>(e =>
        {
            e.HasKey(m => m.MessageId);
        });

        b.Entity<OutboxMessage>(e =>
        {
            e.HasKey(m => m.MessageId);
        });

        // MassTransit saga state machine table
        b.Entity<TransferState>(e =>
        {
            e.HasKey(x => x.CorrelationId);
            e.Property(x => x.CurrentState).HasMaxLength(64);
            e.Property(x => x.Amount).HasColumnType("numeric(18,4)");
            e.Property(x => x.RowVersion).IsRowVersion();
        });

        // MassTransit outbox/inbox tables (SQL Server-compatible)
        b.AddInboxStateEntity();
        b.AddOutboxMessageEntity();
        b.AddOutboxStateEntity();

        // Seed test accounts — RowVersion is omitted: rowversion columns are DB-managed
        b.Entity<Account>().HasData(
            new Account
            {
                AccountId = "ACC-001",
                OwnerId = "user-1",
                Balance = 10000m,
                Currency = "USD",
                IsDeleted = false,
                UpdatedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)
            },
            new Account
            {
                AccountId = "ACC-002",
                OwnerId = "user-2",
                Balance = 5000m,
                Currency = "USD",
                IsDeleted = false,
                UpdatedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)
            }
        );
    }
}
