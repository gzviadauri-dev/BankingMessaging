namespace BankingMessaging.Contracts.Events;

/// <summary>
/// Scheduled by the saga after publishing <see cref="CreditAccountCommand"/>.
/// If <see cref="AccountCreditedEvent"/> does not arrive within the timeout window,
/// the saga transitions to <c>Failed</c> and publishes <see cref="TransferFailedEvent"/>
/// so that <see cref="CompensateDebitTimeoutConsumer"/> can reverse the debit.
/// </summary>
public record TransferCreditTimeout
{
    public Guid CorrelationId { get; init; }
    public Guid TransferId { get; init; }
}
