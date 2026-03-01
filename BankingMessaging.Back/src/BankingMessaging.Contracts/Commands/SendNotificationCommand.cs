namespace BankingMessaging.Contracts.Commands;

public record SendNotificationCommand
{
    public Guid NotificationId { get; init; }
    public string RecipientId { get; init; } = default!;
    public string Channel { get; init; } = default!;  // "email" | "sms" | "push"
    public string Subject { get; init; } = default!;
    public string Body { get; init; } = default!;
}
