namespace BankingMessaging.Infrastructure.Entities;

/// <summary>
/// All valid values for <see cref="Transfer.Status"/>.
/// Use these constants everywhere — never raw string literals — so a rename is caught
/// at compile time rather than as a silent runtime bug.
/// </summary>
/// <remarks>
/// Valid lifecycle transitions:
/// <code>
/// Pending → Debited → Completed  (happy path)
///         ↘         ↘
///          Failed    Failed       (compensation / DLQ)
/// </code>
/// </remarks>
public static class TransferStatus
{
    /// <summary>Transfer created; awaiting debit confirmation from <c>DebitAccountConsumer</c>.</summary>
    public const string Pending   = "Pending";

    /// <summary>Source account debited; credit step in progress.</summary>
    public const string Debited   = "Debited";

    /// <summary>Both debit and credit succeeded. Terminal state — no further transitions.</summary>
    public const string Completed = "Completed";

    /// <summary>
    /// Transfer failed permanently. Debit was reversed by <c>CompensateDebitFaultConsumer</c>
    /// or <c>CompensateDebitTimeoutConsumer</c> if the transfer was in the <see cref="Debited"/> state.
    /// Terminal state — no further transitions.
    /// </summary>
    public const string Failed    = "Failed";
}
