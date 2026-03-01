using BankingMessaging.Infrastructure.Entities;
using BankingMessaging.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MsSql;
using Testcontainers.RabbitMq;

namespace BankingMessaging.IntegrationTests.Infrastructure;

public class BankingTestFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _sqlServer = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
        .WithPassword("Banking123!")
        .Build();

    private readonly RabbitMqContainer _rabbitMq = new RabbitMqBuilder("rabbitmq:3-management")
        .Build();

    public string SqlServerConnectionString => _sqlServer.GetConnectionString();
    public string RabbitMqConnectionString => _rabbitMq.GetConnectionString();

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_sqlServer.StartAsync(), _rabbitMq.StartAsync());

        var services = new ServiceCollection();
        services.AddDbContext<BankingDbContext>(opt =>
            opt.UseSqlServer(SqlServerConnectionString));

        var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BankingDbContext>();
        await db.Database.MigrateAsync();

        if (!await db.Accounts.AnyAsync())
        {
            db.Accounts.AddRange(
                new Account { AccountId = "ACC-001", OwnerId = "user-1", Balance = 10000m, Currency = "USD", UpdatedAt = DateTimeOffset.UtcNow },
                new Account { AccountId = "ACC-002", OwnerId = "user-2", Balance = 5000m, Currency = "USD", UpdatedAt = DateTimeOffset.UtcNow }
            );
            await db.SaveChangesAsync();
        }
    }

    public async Task DisposeAsync()
    {
        await _sqlServer.DisposeAsync();
        await _rabbitMq.DisposeAsync();
    }
}
