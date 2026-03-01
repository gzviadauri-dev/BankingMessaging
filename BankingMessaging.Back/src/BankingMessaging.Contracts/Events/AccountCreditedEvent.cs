namespace BankingMessaging.Contracts.Events;

public record AccountCreditedEvent
{
    public Guid TransferId { get; init; }
    public Guid CorrelationId { get; init; }
    public string AccountId { get; init; } = default!;
    public decimal Amount { get; init; }
    public decimal NewBalance { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
}
