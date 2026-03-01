using MassTransit;

namespace BankingMessaging.Infrastructure.Sagas;

public class TransferState : SagaStateMachineInstance
{
    public Guid CorrelationId { get; set; }
    public string CurrentState { get; set; } = default!;
    public Guid TransferId { get; set; }
    public string FromAccountId { get; set; } = default!;
    public string ToAccountId { get; set; } = default!;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = default!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? FailureReason { get; set; }
    public byte[]? RowVersion { get; set; }

    // Saga timeout scheduling — stores the token returned by Schedule() so the saga
    // can cancel (Unschedule) the timer when the expected event arrives in time.
    public Guid? DebitTimeoutTokenId { get; set; }
    public Guid? CreditTimeoutTokenId { get; set; }
}
