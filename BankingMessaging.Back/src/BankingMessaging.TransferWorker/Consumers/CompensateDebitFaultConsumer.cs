using BankingMessaging.Contracts.Commands;
using BankingMessaging.Contracts.Events;
using MassTransit;

namespace BankingMessaging.TransferWorker.Consumers;

/// <summary>
/// Handles <see cref="Fault{CreditAccountCommand}"/> — triggered when the credit endpoint
/// exhausts all broker retries. Reverses the source account debit and then publishes
/// <see cref="TransferFailedEvent"/> so the saga (still in <c>Debited</c> state) can finalize.
/// </summary>
/// <remarks>
/// <para><b>Why a separate class from <see cref="CompensateDebitTimeoutConsumer"/>:</b>
/// If a single class implements both <c>IConsumer&lt;Fault&lt;CreditAccountCommand&gt;&gt;</c>
/// and <c>IConsumer&lt;TransferFailedEvent&gt;</c>, MassTransit binds the endpoint queue to
/// <em>both</em> exchanges. That means every <c>TransferFailedEvent</c> is also delivered to
/// the fault handler queue, and every <c>Fault&lt;CreditAccountCommand&gt;</c> is also delivered
/// to the timeout handler queue — each message processed twice. Two concurrent calls to
/// <see cref="CompensateDebitService.ReverseDebit"/> would race on the Transfer row even
/// with the UPDLOCK guard in place. Separate classes ensure each queue binds to exactly
/// one exchange and each message is processed exactly once.</para>
///
/// <para><b>No broker-level retry:</b> <see cref="CompensateDebitService.ReverseDebit"/> has
/// its own 5-attempt internal loop. Broker retry would re-run the full method before the
/// previous attempt's transaction commits, risking a second compensation.</para>
/// </remarks>
public sealed class CompensateDebitFaultConsumer : IConsumer<Fault<CreditAccountCommand>>
{
    private readonly CompensateDebitService _service;
    private readonly IPublishEndpoint _publish;
    private readonly ILogger<CompensateDebitFaultConsumer> _logger;

    public CompensateDebitFaultConsumer(
        CompensateDebitService service,
        IPublishEndpoint publish,
        ILogger<CompensateDebitFaultConsumer> logger)
    {
        _service = service;
        _publish  = publish;
        _logger   = logger;
    }

    public async Task Consume(ConsumeContext<Fault<CreditAccountCommand>> context)
    {
        var failed = context.Message.Message;
        var reason = context.Message.Exceptions.FirstOrDefault()?.Message ?? "Unknown";

        _logger.LogCritical(
            "Credit permanently failed for TransferId={TransferId} AccountId={AccountId} Amount={Amount}. " +
            "Initiating compensating debit reversal. Reason: {Reason}",
            failed.TransferId, failed.AccountId, failed.Amount, reason);

        bool reversed = await _service.ReverseDebit(
            transferId: failed.TransferId,
            reason:     $"Credit to destination permanently failed — debit reversed. Original error: {reason}",
            ct:         context.CancellationToken);

        // Notify the saga (still in Debited state) to finalise.
        // RequiresDebitReversal=false: the reversal was already performed above; the
        // CompensateDebitTimeoutConsumer must not act on this event if it also receives it.
        // Only publish when reversal was actually performed to avoid duplicate saga transitions
        // on re-delivery after a partial failure.
        if (reversed)
        {
            await _publish.Publish(new TransferFailedEvent
            {
                TransferId            = failed.TransferId,
                CorrelationId         = failed.CorrelationId,
                Reason                = $"Credit permanently failed, debit reversed: {reason}",
                FailedAt              = DateTimeOffset.UtcNow,
                RequiresDebitReversal = false   // reversal already done by this consumer
            }, context.CancellationToken);
        }
    }
}
