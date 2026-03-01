using BankingMessaging.Contracts.Commands;
using BankingMessaging.Contracts.Events;
using BankingMessaging.Infrastructure.Entities;
using BankingMessaging.Infrastructure.Exceptions;
using BankingMessaging.Infrastructure.Persistence;
using MassTransit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace BankingMessaging.TransferWorker.Consumers;

public class InitiateTransferConsumer : IConsumer<InitiateTransferCommand>
{
    private readonly BankingDbContext _db;
    private readonly IPublishEndpoint _publish;
    private readonly ILogger<InitiateTransferConsumer> _logger;

    public InitiateTransferConsumer(
        BankingDbContext db,
        IPublishEndpoint publish,
        ILogger<InitiateTransferConsumer> logger)
    {
        _db = db;
        _publish = publish;
        _logger = logger;
    }

    /// <summary>
    /// Validates an <see cref="InitiateTransferCommand"/> and publishes <see cref="TransferInitiatedEvent"/>
    /// to kick off the saga workflow.
    /// </summary>
    /// <remarks>
    /// <para><b>Idempotency:</b> Uses insert-and-catch on the <c>InboxMessages</c> table. The PK
    /// unique constraint on <c>MessageId</c> is the atomic guard — not a check-then-insert (TOCTOU).
    /// Re-delivery of the same <c>MessageId</c> hits the unique constraint and is silently discarded.</para>
    ///
    /// <para><b>Retry policy:</b> 5 attempts, exponential back-off (2s → 60s).
    /// <see cref="InsufficientFundsException"/> and <see cref="AccountNotFoundException"/> are NOT retried.
    /// <see cref="TransientDatabaseException"/> IS retried.</para>
    ///
    /// <para><b>On permanent failure:</b> <see cref="Fault{T}"/> routed to DLQ.
    /// <see cref="DeadLetterConsumer"/> marks the transfer as <c>Failed</c>.</para>
    /// </remarks>
    public async Task Consume(ConsumeContext<InitiateTransferCommand> context)
    {
        var msg = context.Message;
        var messageId = context.MessageId!.Value;

        using var scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["TransferId"] = msg.TransferId,
            ["CorrelationId"] = msg.CorrelationId
        });

        // ── IDEMPOTENCY: Atomic insert-and-catch (no TOCTOU race) ─────────────────────
        // Attempt to insert the inbox record first. The PK unique constraint on MessageId
        // is the sole atomic guard. If two concurrent redeliveries arrive simultaneously,
        // exactly one INSERT succeeds; the other hits the unique constraint and returns here.
        var inboxEntry = new InboxMessage
        {
            MessageId = messageId,
            MessageType = nameof(InitiateTransferCommand),
            ReceivedAt = DateTimeOffset.UtcNow
        };

        try
        {
            _db.InboxMessages.Add(inboxEntry);
            await _db.SaveChangesAsync(context.CancellationToken);
        }
        catch (DbUpdateException ex) when (IsDuplicateKeyException(ex))
        {
            _logger.LogWarning(
                "Duplicate message {MessageId} for transfer {TransferId} — skipping (already processed). CorrelationId={CorrelationId}",
                messageId, msg.TransferId, msg.CorrelationId);
            _db.ChangeTracker.Clear();
            return;
        }

        // ── BUSINESS LOGIC (only reached once per MessageId) ─────────────────────────
        if (msg.SimulateError)
        {
            _logger.LogWarning(
                "SimulateError flag set for transfer {TransferId} — throwing transient exception to demo retry. CorrelationId={CorrelationId}",
                msg.TransferId, msg.CorrelationId);
            throw new TransientDatabaseException(
                "Simulated transient error (SimulateError=true) — MassTransit will retry this message");
        }

        _logger.LogInformation(
            "Processing InitiateTransfer for {TransferId} from {From} to {To} for {Amount} {Currency}. CorrelationId={CorrelationId}",
            msg.TransferId, msg.FromAccountId, msg.ToAccountId, msg.Amount, msg.Currency, msg.CorrelationId);

        var fromAccount = await _db.Accounts
            .FirstOrDefaultAsync(a => a.AccountId == msg.FromAccountId && !a.IsDeleted,
                context.CancellationToken)
            ?? throw new AccountNotFoundException(msg.FromAccountId);

        if (fromAccount.Balance < msg.Amount)
            throw new InsufficientFundsException(msg.FromAccountId, msg.Amount);

        inboxEntry.ProcessedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(context.CancellationToken);

        await _publish.Publish(new TransferInitiatedEvent
        {
            TransferId    = msg.TransferId,
            CorrelationId = msg.CorrelationId,
            FromAccountId = msg.FromAccountId,
            ToAccountId   = msg.ToAccountId,
            Amount        = msg.Amount,
            OccurredAt    = DateTimeOffset.UtcNow
        }, context.CancellationToken);

        _logger.LogInformation(
            "Transfer {TransferId} initiated successfully. CorrelationId={CorrelationId}",
            msg.TransferId, msg.CorrelationId);
    }

    // SQL Server unique constraint violation: error 2627 (PK) or 2601 (unique index)
    private static bool IsDuplicateKeyException(DbUpdateException ex) =>
        ex.InnerException is SqlException sqlEx && sqlEx.Number is 2627 or 2601;
}
