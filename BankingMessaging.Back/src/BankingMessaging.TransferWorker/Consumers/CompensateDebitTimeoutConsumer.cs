using BankingMessaging.Contracts.Events;
using MassTransit;

namespace BankingMessaging.TransferWorker.Consumers;

/// <summary>
/// Handles <see cref="TransferFailedEvent"/> with <c>RequiresDebitReversal = true</c> —
/// published by the saga when the credit timeout fires while the saga is in the <c>Debited</c>
/// state. Reverses the source account debit.
/// </summary>
/// <remarks>
/// <para><b>Why a separate class from <see cref="CompensateDebitFaultConsumer"/>:</b>
/// See <see cref="CompensateDebitFaultConsumer"/> for a full explanation. In summary: one class
/// per message type keeps each endpoint queue bound to exactly one exchange, preventing
/// double-processing and eliminating the concurrent-compensation race.</para>
///
/// <para><b>Skipping non-reversal events:</b> All <see cref="TransferFailedEvent"/> messages
/// arrive at this endpoint — including those published by <see cref="CompensateDebitFaultConsumer"/>
/// after a successful fault-path reversal (<c>RequiresDebitReversal = false</c>). These are
/// silently skipped via the guard at the top of <see cref="Consume"/>.</para>
///
/// <para><b>No re-publish:</b> The saga already transitioned to <c>Failed</c> and finalized
/// when it published this event. Publishing another <see cref="TransferFailedEvent"/> here
/// would create a message with no consumer (saga is gone), cluttering the broker.</para>
///
/// <para><b>No broker-level retry:</b> Same reasoning as <see cref="CompensateDebitFaultConsumer"/>.</para>
/// </remarks>
public sealed class CompensateDebitTimeoutConsumer : IConsumer<TransferFailedEvent>
{
    private readonly CompensateDebitService _service;
    private readonly ILogger<CompensateDebitTimeoutConsumer> _logger;

    public CompensateDebitTimeoutConsumer(
        CompensateDebitService service,
        ILogger<CompensateDebitTimeoutConsumer> logger)
    {
        _service = service;
        _logger  = logger;
    }

    public async Task Consume(ConsumeContext<TransferFailedEvent> context)
    {
        var msg = context.Message;

        // Guard: only act when the saga explicitly signals that a debit reversal is needed.
        // RequiresDebitReversal=false means failure occurred before or outside the Debited state
        // (e.g., debit timeout, insufficient funds, or the Fault path already compensated).
        if (!msg.RequiresDebitReversal)
        {
            _logger.LogDebug(
                "TransferFailedEvent for TransferId={TransferId} has RequiresDebitReversal=false — skipping compensation",
                msg.TransferId);
            return;
        }

        _logger.LogCritical(
            "TransferFailedEvent with RequiresDebitReversal=true for TransferId={TransferId}. Reason: {Reason}. " +
            "Initiating compensating debit reversal.",
            msg.TransferId, msg.Reason);

        // The saga already published this event and transitioned to Failed.
        // Do NOT re-publish another TransferFailedEvent — the saga instance is already gone.
        await _service.ReverseDebit(
            transferId: msg.TransferId,
            reason:     $"Credit timed out — debit reversed: {msg.Reason}",
            ct:         context.CancellationToken);
    }
}
