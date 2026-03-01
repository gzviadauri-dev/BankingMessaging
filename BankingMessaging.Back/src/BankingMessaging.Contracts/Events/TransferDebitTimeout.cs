namespace BankingMessaging.Contracts.Events;

/// <summary>
/// Scheduled by the saga after publishing <see cref="DebitAccountCommand"/>.
/// If <see cref="AccountDebitedEvent"/> does not arrive within the timeout window,
/// the saga transitions to <c>Failed</c> without leaving the transfer in limbo.
/// </summary>
public record TransferDebitTimeout
{
    public Guid CorrelationId { get; init; }
    public Guid TransferId { get; init; }
}
