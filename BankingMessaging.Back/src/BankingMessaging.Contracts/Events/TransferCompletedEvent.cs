namespace BankingMessaging.Contracts.Events;

public record TransferCompletedEvent
{
    public Guid TransferId { get; init; }
    public Guid CorrelationId { get; init; }
    public DateTimeOffset CompletedAt { get; init; }
}
