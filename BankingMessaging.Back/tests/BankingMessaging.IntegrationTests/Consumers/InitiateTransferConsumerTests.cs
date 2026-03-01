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

public class InitiateTransferConsumerTests : IAsyncLifetime
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
            x.AddConsumer<InitiateTransferConsumer>();
        });

        _services = services.BuildServiceProvider(true);
        _harness = _services.GetRequiredService<ITestHarness>();
        await _harness.Start();

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BankingDbContext>();
        await db.Database.EnsureCreatedAsync();

        db.Accounts.Add(new Account
        {
            AccountId = "ACC-001",
            OwnerId = "user-1",
            Balance = 10000m,
            Currency = "USD",
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _harness.Stop();
        await _sqlServer.DisposeAsync();
        if (_services is IAsyncDisposable ad) await ad.DisposeAsync();
    }

    [Fact]
    public async Task Consume_ValidTransfer_PublishesTransferInitiatedEvent()
    {
        var transferId = NewId.NextGuid();
        var correlationId = NewId.NextGuid();

        await _harness.Bus.Publish(new InitiateTransferCommand
        {
            TransferId = transferId,
            CorrelationId = correlationId,
            FromAccountId = "ACC-001",
            ToAccountId = "ACC-002",
            Amount = 500m,
            Currency = "USD",
            RequestedBy = "test-user",
            RequestedAt = DateTimeOffset.UtcNow
        });

        Assert.True(await _harness.Consumed.Any<InitiateTransferCommand>());
        Assert.True(await _harness.Published.Any<TransferInitiatedEvent>());
    }

    [Fact]
    public async Task Consume_Idempotent_DuplicateMessageIgnored()
    {
        var messageId = NewId.NextGuid();
        var command = new InitiateTransferCommand
        {
            TransferId = NewId.NextGuid(),
            CorrelationId = NewId.NextGuid(),
            FromAccountId = "ACC-001",
            ToAccountId = "ACC-002",
            Amount = 100m,
            Currency = "USD",
            RequestedBy = "test-user",
            RequestedAt = DateTimeOffset.UtcNow
        };

        await _harness.Bus.Publish(command, x => x.MessageId = messageId);
        await Task.Delay(300);
        await _harness.Bus.Publish(command, x => x.MessageId = messageId);
        await Task.Delay(300);

        int eventCount = 0;
        await foreach (var _ in _harness.Published.SelectAsync<TransferInitiatedEvent>())
            eventCount++;

        Assert.Equal(1, eventCount);
    }

    [Fact]
    public async Task Consume_InsufficientFunds_PublishesFault()
    {
        await _harness.Bus.Publish(new InitiateTransferCommand
        {
            TransferId = NewId.NextGuid(),
            CorrelationId = NewId.NextGuid(),
            FromAccountId = "ACC-001",
            ToAccountId = "ACC-002",
            Amount = 999999m,
            Currency = "USD",
            RequestedBy = "test-user",
            RequestedAt = DateTimeOffset.UtcNow
        });

        await Task.Delay(500);

        Assert.True(await _harness.Published.Any<Fault<InitiateTransferCommand>>());
        Assert.False(await _harness.Published.Any<TransferInitiatedEvent>());
    }
}
