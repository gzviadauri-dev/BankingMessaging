namespace BankingMessaging.Contracts.Commands;

public record InitiateTransferCommand
{
    public Guid TransferId { get; init; }
    public Guid CorrelationId { get; init; }
    public string FromAccountId { get; init; } = string.Empty;
    public string ToAccountId { get; init; } = string.Empty;
    public decimal Amount { get; init; }
    public string Currency { get; init; } = "USD";       // safe default — old producers omit, consumers read "USD"
    public string RequestedBy { get; init; } = string.Empty;
    public DateTimeOffset RequestedAt { get; init; }
    public bool SimulateError { get; init; }

    // Example of a safely-added field: nullable, consumers must null-check before use.
    // Old producers won't include it; deserialization produces null rather than throwing.
    public string? IdempotencyKey { get; init; }
}
