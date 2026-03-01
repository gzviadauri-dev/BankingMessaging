using BankingMessaging.Contracts.Commands;
using MassTransit;

namespace BankingMessaging.NotificationWorker.Consumers;

public class SendNotificationConsumer : IConsumer<SendNotificationCommand>
{
    private readonly ILogger<SendNotificationConsumer> _logger;

    public SendNotificationConsumer(ILogger<SendNotificationConsumer> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Delivers a notification (email / SMS / push) for a completed or failed transfer.
    /// </summary>
    /// <remarks>
    /// <para><b>Retry policy:</b> 3 fixed-interval attempts (5s, 15s, 60s) on the endpoint.
    /// All exceptions are retried — notification delivery is inherently idempotent (duplicate
    /// notifications are an acceptable trade-off vs. missed notifications in a banking context).</para>
    ///
    /// <para><b>No DB dependency:</b> This service intentionally has no reference to
    /// <c>BankingMessaging.Infrastructure</c>. All required data arrives in the message payload.</para>
    /// </remarks>
    public async Task Consume(ConsumeContext<SendNotificationCommand> context)
    {
        var msg = context.Message;

        _logger.LogInformation(
            "Sending {Channel} notification to {RecipientId}: {Subject}. NotificationId={NotificationId}",
            msg.Channel, msg.RecipientId, msg.Subject, msg.NotificationId);

        // Simulate sending — replace with real email/SMS/push SDK
        await Task.Delay(100, context.CancellationToken);

        _logger.LogInformation(
            "Notification {NotificationId} sent via {Channel} to {RecipientId}",
            msg.NotificationId, msg.Channel, msg.RecipientId);
    }
}
