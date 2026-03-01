using System.Data;
using BankingMessaging.Infrastructure.Entities;
using BankingMessaging.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BankingMessaging.TransferWorker.Consumers;

/// <summary>
/// Shared reversal logic used by both <see cref="CompensateDebitFaultConsumer"/> and
/// <see cref="CompensateDebitTimeoutConsumer"/>.
/// </summary>
/// <remarks>
/// Registered as a scoped DI service so each MassTransit message scope gets its own
/// instance tied to the same <see cref="BankingDbContext"/> as the consumer.
/// </remarks>
public sealed class CompensateDebitService
{
    private readonly BankingDbContext _db;
    private readonly ILogger<CompensateDebitService> _logger;

    public CompensateDebitService(BankingDbContext db, ILogger<CompensateDebitService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Reverses the debit on the source account and marks the transfer as
    /// <see cref="TransferStatus.Failed"/>.
    /// Both the balance update and the status change are committed in one atomic transaction.
    /// </summary>
    /// <param name="transferId">The transfer to compensate.</param>
    /// <param name="reason">Human-readable reason stored on <c>Transfer.FailureReason</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <c>true</c> when the reversal was actually performed (transfer was in
    /// <see cref="TransferStatus.Debited"/> state);
    /// <c>false</c> when skipped (transfer not found or already in a terminal state).
    /// </returns>
    /// <remarks>
    /// <para><b>Serialisation:</b> The Transfer row is read with <c>UPDLOCK + ROWLOCK</c> inside a
    /// <c>RepeatableRead</c> transaction. This serialises ALL concurrent compensation calls
    /// (e.g., the Fault path and the CreditTimeout path arriving simultaneously). Only the first
    /// caller ever sees <c>Status == "Debited"</c>; every subsequent caller sees
    /// <c>Status == "Failed"</c> and exits silently — no deadlock, no double-reversal.</para>
    ///
    /// <para><b>Atomicity:</b> <c>sourceAccount.Balance += amount</c> and
    /// <c>transfer.Status = "Failed"</c> are both flushed in a single <c>SaveChangesAsync</c>
    /// before <c>CommitAsync</c>. A crash between the two calls rolls the transaction back;
    /// on redelivery the Transfer is still <c>"Debited"</c> so compensation is retried safely.</para>
    ///
    /// <para><b>Amount source:</b> The amount is read from the locked Transfer row — not passed
    /// as a parameter — so the authoritative DB value is always used.</para>
    /// </remarks>
    public async Task<bool> ReverseDebit(Guid transferId, string reason, CancellationToken ct)
    {
        const int maxAttempts = 5;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            await using var tx = await _db.Database.BeginTransactionAsync(
                IsolationLevel.RepeatableRead, ct);
            try
            {
                // ── Lock the Transfer row FIRST ───────────────────────────────────────────
                // UPDLOCK + ROWLOCK serialises all concurrent compensation attempts on this
                // transfer. The first caller acquires the lock and proceeds; all others block
                // until the first commits, then read Status == "Failed" and skip.
                // Without this lock, two concurrent callers can both read Status == "Debited",
                // both proceed, and double-reverse the balance.
                var transfer = await _db.Transfers
                    .FromSqlRaw(
                        "SELECT * FROM Transfers WITH (UPDLOCK, ROWLOCK) WHERE TransferId = {0}",
                        transferId)
                    .FirstOrDefaultAsync(ct);

                if (transfer is null)
                {
                    _logger.LogError(
                        "Cannot compensate: Transfer {TransferId} not found in DB", transferId);
                    await tx.RollbackAsync(ct);
                    return false;
                }

                // ── Idempotency guard ─────────────────────────────────────────────────────
                // Only compensate from the Debited state:
                //   Pending   → debit never ran      → no reversal needed
                //   Debited   → debit happened        → reversal required  ← this path
                //   Completed → credit succeeded      → NEVER reverse
                //   Failed    → already compensated   → skip (idempotent re-delivery)
                if (transfer.Status != TransferStatus.Debited)
                {
                    _logger.LogWarning(
                        "Compensation skipped for TransferId={TransferId}: " +
                        "Status={Status} (expected '{Expected}'). Already compensated or in unexpected state.",
                        transferId, transfer.Status, TransferStatus.Debited);
                    await tx.RollbackAsync(ct);
                    return false;
                }

                // ⚠️  RAW SQL — EF CORE GLOBAL FILTERS ARE BYPASSED HERE ⚠️
                // This query manually replicates ALL active global filters on Account.
                // UPDLOCK + ROWLOCK hints cannot be applied through EF Core's LINQ pipeline.
                //
                // IF YOU:
                //   • Rename the IsDeleted column → update SQL here, in DebitAccountConsumer,
                //     and in CreditAccountConsumer
                //   • Add a new global filter to Account (e.g. TenantId, IsArchived) →
                //     add it to the SQL in all three consumers
                //   • Remove soft-delete from Account → remove the IsDeleted condition
                //
                // SEARCH: "RAW SQL — EF CORE GLOBAL FILTERS ARE BYPASSED" to find all locations.
                var sourceAccount = await _db.Accounts
                    .FromSqlRaw(
                        @"SELECT * FROM Accounts WITH (UPDLOCK, ROWLOCK)
                          WHERE AccountId = {0}
                            AND IsDeleted = 0",   // ← manually replicates soft-delete global filter
                        transfer.FromAccountId)
                    .FirstOrDefaultAsync(ct);

                if (sourceAccount is null)
                {
                    _logger.LogCritical(
                        "CRITICAL: Source account {AccountId} not found during compensation " +
                        "for TransferId={TransferId}. MANUAL INTERVENTION REQUIRED.",
                        transfer.FromAccountId, transferId);

                    // Mark the transfer Failed even though the balance could not be restored.
                    // Leaving it in Debited would make it appear as still-pending compensation.
                    transfer.Status        = TransferStatus.Failed;
                    transfer.FailureReason = $"MANUAL REVERSAL REQUIRED — source account not found. Original: {reason}";
                    transfer.CompletedAt   = DateTimeOffset.UtcNow;
                    await _db.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);
                    return false;
                }

                // ── Atomic reversal ───────────────────────────────────────────────────────
                // Both changes (balance + status) are flushed in ONE SaveChangesAsync.
                // If the process crashes before CommitAsync the transaction rolls back;
                // on redelivery, transfer.Status is still "Debited" → compensation retries.
                var amount = transfer.Amount;   // authoritative from locked DB row
                sourceAccount.Balance     += amount;
                sourceAccount.UpdatedAt    = DateTimeOffset.UtcNow;
                transfer.Status            = TransferStatus.Failed;
                transfer.FailureReason     = reason;
                transfer.CompletedAt       = DateTimeOffset.UtcNow;

                await _db.SaveChangesAsync(ct);   // ONE call — covers account balance + transfer status
                await tx.CommitAsync(ct);          // transaction committed atomically

                _logger.LogWarning(
                    "Compensation COMPLETE: Reversed debit of {Amount} on account {AccountId} " +
                    "for TransferId={TransferId}. Transfer marked {Status}.",
                    amount, transfer.FromAccountId, transferId, TransferStatus.Failed);

                return true;
            }
            catch (DbUpdateConcurrencyException ex) when (attempt < maxAttempts)
            {
                await tx.RollbackAsync(ct);

                // Detach only the stale Account entities — preserve Transfer tracked state.
                foreach (var entry in ex.Entries.Where(e => e.Entity is Account))
                    entry.State = EntityState.Detached;

                _logger.LogWarning(
                    "Concurrency conflict during compensation attempt {Attempt}/{Max} " +
                    "for TransferId={TransferId}",
                    attempt, maxAttempts, transferId);

                await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), ct);
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        }

        _logger.LogCritical(
            "CRITICAL: Compensation FAILED after {MaxAttempts} attempts for TransferId={TransferId}. " +
            "Source account balance may remain debited. MANUAL INTERVENTION REQUIRED.",
            maxAttempts, transferId);

        return false;
    }
}
