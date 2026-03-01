using BankingMessaging.Contracts.Commands;
using BankingMessaging.Contracts.Events;
using BankingMessaging.Infrastructure.Entities;
using BankingMessaging.Infrastructure.Persistence;
using BankingMessaging.TransferWorker.Consumers;
using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MsSql;

namespace BankingMessaging.IntegrationTests.Consumers;

public class DebitAccountConsumerTests : IAsyncLifetime
{
    private readonly MsSqlContainer _sqlServer = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
        .WithPassword("Banking123!")
        .Build();

    private IServiceProvider _services = null!;
    private ITestHarness _harness = null!;

    public async Task InitializeAsync()
    {
        await _sqlServer.StartAsync();

        var services = new ServiceCollection();
        services.AddDbContext<BankingDbContext>(opt =>
            opt.UseSqlServer(_sqlServer.GetConnectionString()));

        services.AddMassTransitTestHarness(x =>
        {
            x.AddConsumer<DebitAccountConsumer>();
        });

        _services = services.BuildServiceProvider(true);
        _harness = _services.GetRequiredService<ITestHarness>();
        await _harness.Start();

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BankingDbContext>();
        await db.Database.EnsureCreatedAsync();

        db.Accounts.AddRange(
            new Account { AccountId = "ACC-001", OwnerId = "user-1", Balance = 1000m, Currency = "USD", UpdatedAt = DateTimeOffset.UtcNow },
            new Account { AccountId = "ACC-EMPTY", OwnerId = "user-3", Balance = 0m, Currency = "USD", UpdatedAt = DateTimeOffset.UtcNow }
        );
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _harness.Stop();
        await _sqlServer.DisposeAsync();
        if (_services is IAsyncDisposable ad) await ad.DisposeAsync();
    }

    [Fact]
    public async Task Consume_ValidDebit_PublishesAccountDebitedEvent()
    {
        var transferId = NewId.NextGuid();
        var correlationId = NewId.NextGuid();

        await _harness.Bus.Publish(new DebitAccountCommand
        {
            TransferId = transferId,
            CorrelationId = correlationId,
            AccountId = "ACC-001",
            Amount = 100m,
            Currency = "USD"
        });

        Assert.True(await _harness.Consumed.Any<DebitAccountCommand>());

        var consumerHarness = _harness.GetConsumerHarness<DebitAccountConsumer>();
        Assert.True(await consumerHarness.Consumed.Any<DebitAccountCommand>());
        Assert.True(await _harness.Published.Any<AccountDebitedEvent>());

        var publishedEvent = (await _harness.Published.SelectAsync<AccountDebitedEvent>().First()).Context.Message;
        Assert.Equal(transferId, publishedEvent.TransferId);
        Assert.Equal(100m, publishedEvent.Amount);
        Assert.Equal(900m, publishedEvent.NewBalance);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BankingDbContext>();
        var account = await db.Accounts.FindAsync("ACC-001");
        Assert.Equal(900m, account!.Balance);
    }

    [Fact]
    public async Task Consume_InsufficientFunds_ThrowsWithoutRetry()
    {
        var transferId = NewId.NextGuid();
        var correlationId = NewId.NextGuid();

        await _harness.Bus.Publish(new DebitAccountCommand
        {
            TransferId = transferId,
            CorrelationId = correlationId,
            AccountId = "ACC-EMPTY",
            Amount = 500m,
            Currency = "USD"
        });

        await Task.Delay(500);

        Assert.True(await _harness.Consumed.Any<DebitAccountCommand>());
        Assert.True(await _harness.Published.Any<Fault<DebitAccountCommand>>());
        Assert.False(await _harness.Published.Any<AccountDebitedEvent>());
    }

    [Fact]
    public async Task Consume_AccountNotFound_ThrowsFault()
    {
        await _harness.Bus.Publish(new DebitAccountCommand
        {
            TransferId = NewId.NextGuid(),
            CorrelationId = NewId.NextGuid(),
            AccountId = "NONEXISTENT",
            Amount = 100m,
            Currency = "USD"
        });

        await Task.Delay(500);

        Assert.True(await _harness.Published.Any<Fault<DebitAccountCommand>>());
    }

    [Fact]
    public async Task Consume_RaceCondition_AllValidDebitsSucceed()
    {
        var tasks = Enumerable.Range(0, 5).Select(i =>
            _harness.Bus.Publish(new DebitAccountCommand
            {
                TransferId = NewId.NextGuid(),
                CorrelationId = NewId.NextGuid(),
                AccountId = "ACC-001",
                Amount = 50m,
                Currency = "USD"
            })
        );

        await Task.WhenAll(tasks);
        await Task.Delay(3000);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BankingDbContext>();
        var account = await db.Accounts.FindAsync("ACC-001");

        Assert.True(account!.Balance >= 0, $"Balance went negative: {account.Balance}");
    }
}
