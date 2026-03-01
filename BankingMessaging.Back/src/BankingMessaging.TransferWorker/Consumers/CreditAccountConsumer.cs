using System.Data;
using BankingMessaging.Contracts.Commands;
using BankingMessaging.Contracts.Events;
using BankingMessaging.Infrastructure.Entities;
using BankingMessaging.Infrastructure.Exceptions;
using BankingMessaging.Infrastructure.Persistence;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using TransferStatus = BankingMessaging.Infrastructure.Entities.TransferStatus;

namespace BankingMessaging.TransferWorker.Consumers;

public class CreditAccountConsumer : IConsumer<CreditAccountCommand>
{
    private readonly BankingDbContext _db;
    private readonly IPublishEndpoint _publish;
    private readonly ILogger<CreditAccountConsumer> _logger;

    public CreditAccountConsumer(
        BankingDbContext db,
        IPublishEndpoint publish,
        ILogger<CreditAccountConsumer> logger)
    {
        _db = db;
        _publish = publish;
        _logger = logger;
    }

    /// <summary>
    /// Credits the destination account and publishes <see cref="AccountCreditedEvent"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>Race condition safety (three layers):</b>
    /// <list type="number">
    ///   <item><c>RepeatableRead</c> isolation — prevents non-repeatable reads within the transaction.</item>
    ///   <item><c>SELECT WITH (UPDLOCK, ROWLOCK)</c> — serialises concurrent credits on the same account.</item>
    ///   <item><c>Account.RowVersion</c> (SQL Server <c>rowversion</c>) — DB-generated concurrency token.
    ///     On mismatch, only the stale account entity is detached and the loop retries.</item>
    /// </list></para>
    ///
    /// <para><b>On permanent failure:</b> <see cref="Fault{CreditAccountCommand}"/> is routed to
    /// <see cref="CompensateDebitFaultConsumer"/>, which reverses the source account debit to prevent money loss.
    /// This consumer does NOT attempt compensation itself.</para>
    ///
    /// <para><b>Retry policy:</b> 3 attempts, exponential back-off (1s → 15s) on the endpoint.
    /// <see cref="AccountNotFoundException"/> is NOT retried. <see cref="TransientDatabaseException"/> IS retried.</para>
    /// </remarks>
    public async Task Consume(ConsumeContext<CreditAccountCommand> context)
    {
        var msg = context.Message;

        using var scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["TransferId"] = msg.TransferId,
            ["CorrelationId"] = msg.CorrelationId
        });

        _logger.LogInformation(
            "Crediting {Amount} {Currency} to account {AccountId}. TransferId={TransferId} CorrelationId={CorrelationId}",
            msg.Amount, msg.Currency, msg.AccountId, msg.TransferId, msg.CorrelationId);

        const int maxAttempts = 3;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(
                IsolationLevel.RepeatableRead, context.CancellationToken);

            try
            {
                // ⚠️  RAW SQL — EF CORE GLOBAL FILTERS ARE BYPASSED HERE ⚠️
                // This query manually replicates ALL active global filters on the Account entity.
                // UPDLOCK + ROWLOCK hints cannot be applied through EF Core's LINQ pipeline.
                //
                // IF YOU:
                //   • Rename the IsDeleted column → update the SQL below in BOTH DebitAccountConsumer
                //     and CreditAccountConsumer (and CompensateDebitService)
                //   • Add a new global filter to Account (e.g. TenantId, IsArchived) →
                //     add it to the SQL in all three consumers
                //   • Remove soft-delete from Account → remove the IsDeleted condition below
                //
                // SEARCH: "RAW SQL — EF CORE GLOBAL FILTERS ARE BYPASSED" to find all locations.
                var account = await _db.Accounts
                    .FromSqlRaw(
                        @"SELECT * FROM Accounts WITH (UPDLOCK, ROWLOCK)
                          WHERE AccountId = {0}
                            AND IsDeleted = 0",   // ← manually replicates soft-delete global filter
                        msg.AccountId)
                    .FirstOrDefaultAsync(context.CancellationToken)
                    ?? throw new AccountNotFoundException(msg.AccountId);

                var previousBalance = account.Balance;
                account.Balance += msg.Amount;
                // RowVersion is a SQL Server rowversion column — DB increments atomically on UPDATE.
                account.UpdatedAt = DateTimeOffset.UtcNow;

                await _db.SaveChangesAsync(context.CancellationToken);
                await transaction.CommitAsync(context.CancellationToken);

                var transfer = await _db.Transfers.FindAsync([msg.TransferId], context.CancellationToken);
                if (transfer is not null)
                {
                    transfer.Status      = TransferStatus.Completed;
                    transfer.CompletedAt = DateTimeOffset.UtcNow;
                    await _db.SaveChangesAsync(context.CancellationToken);
                }

                _logger.LogInformation(
                    "Account {AccountId} credited. Balance: {PreviousBalance} → {NewBalance}. TransferId={TransferId} CorrelationId={CorrelationId}",
                    msg.AccountId, previousBalance, account.Balance, msg.TransferId, msg.CorrelationId);

                await _publish.Publish(new AccountCreditedEvent
                {
                    TransferId    = msg.TransferId,
                    CorrelationId = msg.CorrelationId,
                    AccountId     = msg.AccountId,
                    Amount        = msg.Amount,
                    NewBalance    = account.Balance,
                    OccurredAt    = DateTimeOffset.UtcNow
                }, context.CancellationToken);

                return;
            }
            catch (DbUpdateConcurrencyException ex) when (attempt < maxAttempts)
            {
                await transaction.RollbackAsync(context.CancellationToken);

                // Only detach the stale Account entity — do not wipe the full change tracker.
                foreach (var entry in ex.Entries)
                {
                    if (entry.Entity is Account)
                        entry.State = EntityState.Detached;
                }

                _logger.LogWarning(
                    "Concurrency conflict on account {AccountId} — retrying (attempt {Attempt}/{Max}) in {Delay}ms. TransferId={TransferId} CorrelationId={CorrelationId}",
                    msg.AccountId, attempt, maxAttempts, 50 * attempt, msg.TransferId, msg.CorrelationId);

                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), context.CancellationToken);
            }
            catch
            {
                await transaction.RollbackAsync(context.CancellationToken);
                throw;
            }
        }

        throw new Infrastructure.Exceptions.ConcurrencyException(
            $"Failed to credit account {msg.AccountId} after {maxAttempts} attempts. TransferId={msg.TransferId}");
    }
}
