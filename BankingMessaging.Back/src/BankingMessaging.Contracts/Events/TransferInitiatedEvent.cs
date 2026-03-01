namespace BankingMessaging.Contracts.Events;

public record TransferInitiatedEvent
{
    public Guid TransferId { get; init; }
    public Guid CorrelationId { get; init; }
    public string FromAccountId { get; init; } = default!;
    public string ToAccountId { get; init; } = default!;
    public decimal Amount { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
}
