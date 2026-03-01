using BankingMessaging.Contracts.Commands;
using BankingMessaging.Infrastructure.Entities;
using BankingMessaging.Infrastructure.Persistence;
using MassTransit;

namespace BankingMessaging.TransferWorker.Consumers;

/// <summary>
/// Handles permanently-failed messages (Fault envelopes) that exhausted all retries.
/// Marks the corresponding Transfer as <see cref="TransferStatus.Failed"/> in the database.
/// </summary>
/// <remarks>
/// Does NOT handle <see cref="Fault{CreditAccountCommand}"/> — credit faults are owned by
/// <see cref="CompensateDebitFaultConsumer"/>, which reverses the debit before marking as Failed.
/// </remarks>
public class DeadLetterConsumer :
    IConsumer<Fault<InitiateTransferCommand>>,
    IConsumer<Fault<DebitAccountCommand>>
{
    private readonly BankingDbContext _db;
    private readonly ILogger<DeadLetterConsumer> _logger;

    public DeadLetterConsumer(BankingDbContext db, ILogger<DeadLetterConsumer> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<Fault<InitiateTransferCommand>> context)
    {
        var reason = context.Message.Exceptions.FirstOrDefault()?.Message;
        await HandleFault(
            context.Message.Message.TransferId,
            context.Message.Message.CorrelationId,
            reason,
            context.CancellationToken);
    }

    public async Task Consume(ConsumeContext<Fault<DebitAccountCommand>> context)
    {
        var reason = context.Message.Exceptions.FirstOrDefault()?.Message;
        await HandleFault(
            context.Message.Message.TransferId,
            context.Message.Message.CorrelationId,
            reason,
            context.CancellationToken);
    }

    private async Task HandleFault(
        Guid transferId,
        Guid correlationId,
        string? reason,
        CancellationToken ct)
    {
        _logger.LogError(
            "Transfer {TransferId} permanently failed: {Reason}. CorrelationId={CorrelationId}",
            transferId, reason, correlationId);

        var transfer = await _db.Transfers.FindAsync([transferId], ct);
        if (transfer is not null)
        {
            transfer.Status        = TransferStatus.Failed;
            transfer.FailureReason = reason ?? "Unknown error";
            transfer.CompletedAt   = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
    }
}
