namespace BankingMessaging.Infrastructure.Entities;

public class OutboxMessage
{
    public Guid MessageId { get; set; }
    public string MessageType { get; set; } = default!;
    public string Payload { get; set; } = default!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
}
