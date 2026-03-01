namespace BankingMessaging.Contracts.Events;

public record TransferFailedEvent
{
    public Guid TransferId { get; init; }
    public Guid CorrelationId { get; init; }
    public string Reason { get; init; } = string.Empty;
    public DateTimeOffset FailedAt { get; init; }

    /// <summary>
    /// <c>true</c> when the saga was in the <c>Debited</c> state (source account was
    /// already debited) when the credit step failed or timed out.
    /// <see cref="CompensateDebitTimeoutConsumer"/> MUST reverse the debit only when this flag is <c>true</c>.
    /// <br/>
    /// <c>false</c> means failure occurred before any debit — no reversal should happen.
    /// Default is <c>false</c> for backward compatibility: old producers that do not include
    /// this field will never accidentally trigger compensation.
    /// </summary>
    public bool RequiresDebitReversal { get; init; } = false;
}
