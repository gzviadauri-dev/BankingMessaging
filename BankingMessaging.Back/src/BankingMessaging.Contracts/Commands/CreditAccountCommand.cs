namespace BankingMessaging.Contracts.Commands;

public record CreditAccountCommand
{
    public Guid TransferId { get; init; }
    public Guid CorrelationId { get; init; }
    public string AccountId { get; init; } = default!;
    public decimal Amount { get; init; }
    public string Currency { get; init; } = default!;
}
