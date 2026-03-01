using BankingMessaging.Contracts.Commands;
using BankingMessaging.Contracts.Events;
using MassTransit;

namespace BankingMessaging.Infrastructure.Sagas;

public class TransferStateMachine : MassTransitStateMachine<TransferState>
{
    // ── States ────────────────────────────────────────────────────────────────────────
    public State Initiated { get; private set; } = default!;
    public State Debited { get; private set; } = default!;
    public State Completed { get; private set; } = default!;
    public State Failed { get; private set; } = default!;

    // ── Business events ───────────────────────────────────────────────────────────────
    public Event<TransferInitiatedEvent> TransferInitiated { get; private set; } = default!;
    public Event<AccountDebitedEvent> AccountDebited { get; private set; } = default!;
    public Event<AccountCreditedEvent> AccountCredited { get; private set; } = default!;
    public Event<TransferFailedEvent> TransferFailed { get; private set; } = default!;

    // ── Timeout schedules ─────────────────────────────────────────────────────────────
    // DebitTimeout: if AccountDebitedEvent doesn't arrive within 5 min after DebitAccountCommand,
    //               the transfer is abandoned as Failed. No debit happened — no compensation needed.
    public Schedule<TransferState, TransferDebitTimeout> DebitTimeout { get; private set; } = default!;

    // CreditTimeout: if AccountCreditedEvent doesn't arrive within 5 min after CreditAccountCommand,
    //                TransferFailedEvent(RequiresDebitReversal=true) is published →
    //                CompensateDebitTimeoutConsumer reverses the debit.
    public Schedule<TransferState, TransferCreditTimeout> CreditTimeout { get; private set; } = default!;

    public TransferStateMachine()
    {
        InstanceState(x => x.CurrentState);

        // ── Event correlation ────────────────────────────────────────────────────────
        Event(() => TransferInitiated, x => x.CorrelateById(m => m.Message.CorrelationId));
        Event(() => AccountDebited,    x => x.CorrelateById(m => m.Message.CorrelationId));
        Event(() => AccountCredited,   x => x.CorrelateById(m => m.Message.CorrelationId));
        Event(() => TransferFailed,    x => x.CorrelateById(m => m.Message.CorrelationId));

        // ── Timeout schedule definitions ─────────────────────────────────────────────
        Schedule(() => DebitTimeout,
            state => state.DebitTimeoutTokenId,
            s =>
            {
                s.Delay    = TimeSpan.FromMinutes(5);
                s.Received = r => r.CorrelateById(m => m.Message.CorrelationId);
            });

        Schedule(() => CreditTimeout,
            state => state.CreditTimeoutTokenId,
            s =>
            {
                s.Delay    = TimeSpan.FromMinutes(5);
                s.Received = r => r.CorrelateById(m => m.Message.CorrelationId);
            });

        // ── Initially ────────────────────────────────────────────────────────────────
        Initially(
            When(TransferInitiated)
                .Then(ctx =>
                {
                    ctx.Saga.TransferId    = ctx.Message.TransferId;
                    ctx.Saga.FromAccountId = ctx.Message.FromAccountId;
                    ctx.Saga.ToAccountId   = ctx.Message.ToAccountId;
                    ctx.Saga.Amount        = ctx.Message.Amount;
                    ctx.Saga.Currency      = "USD";
                    ctx.Saga.CreatedAt     = DateTimeOffset.UtcNow;
                })
                .Publish(ctx => new DebitAccountCommand
                {
                    TransferId    = ctx.Saga.TransferId,
                    CorrelationId = ctx.Saga.CorrelationId,
                    AccountId     = ctx.Saga.FromAccountId,
                    Amount        = ctx.Saga.Amount,
                    Currency      = ctx.Saga.Currency
                })
                // Start 5-minute watchdog: if DebitAccountConsumer never responds, fail the transfer
                .Schedule(DebitTimeout, ctx => new TransferDebitTimeout
                {
                    CorrelationId = ctx.Saga.CorrelationId,
                    TransferId    = ctx.Saga.TransferId
                })
                .TransitionTo(Initiated)
        );

        // ── During Initiated ─────────────────────────────────────────────────────────
        During(Initiated,
            When(AccountDebited)
                .Unschedule(DebitTimeout)  // debit arrived in time — cancel the watchdog
                .Publish(ctx => new CreditAccountCommand
                {
                    TransferId    = ctx.Saga.TransferId,
                    CorrelationId = ctx.Saga.CorrelationId,
                    AccountId     = ctx.Saga.ToAccountId,
                    Amount        = ctx.Saga.Amount,
                    Currency      = ctx.Saga.Currency
                })
                // Start 5-minute watchdog for credit step
                .Schedule(CreditTimeout, ctx => new TransferCreditTimeout
                {
                    CorrelationId = ctx.Saga.CorrelationId,
                    TransferId    = ctx.Saga.TransferId
                })
                .TransitionTo(Debited),

            // Debit timed out — no response from DebitAccountConsumer within 5 min.
            // Saga is still in Initiated state: debit NEVER happened → RequiresDebitReversal = false.
            When(DebitTimeout!.Received)
                .Then(ctx =>
                {
                    ctx.Saga.FailureReason = "Debit timed out after 5 minutes — no response from DebitAccountConsumer";
                    ctx.Saga.CompletedAt   = DateTimeOffset.UtcNow;
                })
                .Publish(ctx => new TransferFailedEvent
                {
                    TransferId            = ctx.Saga.TransferId,
                    CorrelationId         = ctx.Saga.CorrelationId,
                    Reason                = ctx.Saga.FailureReason!,
                    FailedAt              = DateTimeOffset.UtcNow,
                    RequiresDebitReversal = false   // debit never happened — no reversal needed
                })
                .TransitionTo(Failed)
                .Finalize(),

            // TransferFailed received while in Initiated state (e.g. InsufficientFunds pushed to DLQ).
            // Saga is still in Initiated: debit NEVER happened → RequiresDebitReversal = false.
            When(TransferFailed)
                .Then(ctx => ctx.Saga.FailureReason = ctx.Message.Reason)
                .Publish(ctx => new TransferFailedEvent
                {
                    TransferId            = ctx.Saga.TransferId,
                    CorrelationId         = ctx.Saga.CorrelationId,
                    Reason                = ctx.Saga.FailureReason!,
                    FailedAt              = DateTimeOffset.UtcNow,
                    RequiresDebitReversal = false   // still in Initiated — no debit happened
                })
                .TransitionTo(Failed)
                .Finalize()
        );

        // ── During Debited ───────────────────────────────────────────────────────────
        During(Debited,
            When(AccountCredited)
                .Unschedule(CreditTimeout)  // credit arrived in time — cancel the watchdog
                .Then(ctx => ctx.Saga.CompletedAt = DateTimeOffset.UtcNow)
                .Publish(ctx => new TransferCompletedEvent
                {
                    TransferId    = ctx.Saga.TransferId,
                    CorrelationId = ctx.Saga.CorrelationId,
                    CompletedAt   = ctx.Saga.CompletedAt!.Value
                })
                .Publish(ctx => new SendNotificationCommand
                {
                    NotificationId = NewId.NextGuid(),
                    RecipientId    = ctx.Saga.FromAccountId,
                    Channel        = "email",
                    Subject        = "Transfer completed",
                    Body           = $"Your transfer of {ctx.Saga.Amount} {ctx.Saga.Currency} was completed successfully."
                })
                .TransitionTo(Completed)
                .Finalize(),

            // Credit timed out — saga is in Debited state: debit DID happen → RequiresDebitReversal = true.
            // CompensateDebitTimeoutConsumer handles this event and reverses the debit.
            When(CreditTimeout!.Received)
                .Then(ctx =>
                {
                    ctx.Saga.FailureReason = "Credit timed out after 5 minutes — compensating debit reversal required";
                    ctx.Saga.CompletedAt   = DateTimeOffset.UtcNow;
                })
                .Publish(ctx => new TransferFailedEvent
                {
                    TransferId            = ctx.Saga.TransferId,
                    CorrelationId         = ctx.Saga.CorrelationId,
                    Reason                = ctx.Saga.FailureReason!,
                    FailedAt              = DateTimeOffset.UtcNow,
                    RequiresDebitReversal = true    // debit happened — MUST reverse
                })
                .TransitionTo(Failed)
                .Finalize(),

            // TransferFailedEvent received while in Debited state.
            // This arrives when CompensateDebitFaultConsumer publishes it after handling
            // Fault<CreditAccountCommand> — the reversal has already been performed.
            // Do NOT re-publish: that would trigger a second compensation attempt.
            // Just unschedule the credit watchdog and finalize the saga.
            When(TransferFailed)
                .Unschedule(CreditTimeout)
                .Then(ctx => ctx.Saga.FailureReason = ctx.Message.Reason)
                .TransitionTo(Failed)
                .Finalize()
        );

        SetCompletedWhenFinalized();
    }
}
