namespace BankingMessaging.Infrastructure.Entities;

public class InboxMessage
{
    public Guid MessageId { get; set; }
    public string MessageType { get; set; } = default!;
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
}
