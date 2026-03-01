namespace BankingMessaging.Infrastructure.Entities;

public class Transfer
{
    public Guid TransferId { get; set; }
    public string FromAccountId { get; set; } = default!;
    public string ToAccountId { get; set; } = default!;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = default!;
    public string Status { get; set; } = default!;  // Pending, Debited, Completed, Failed
    public string? FailureReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
