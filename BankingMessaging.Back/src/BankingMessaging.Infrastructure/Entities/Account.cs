namespace BankingMessaging.Infrastructure.Entities;

public class Account
{
    public string AccountId { get; set; } = default!;
    public string OwnerId { get; set; } = default!;
    public decimal Balance { get; set; }
    public string Currency { get; set; } = default!;

    // SQL Server rowversion — incremented atomically by the DB on every UPDATE.
    // EF Core includes this in WHERE on every SaveChanges; mismatch → DbUpdateConcurrencyException.
    // NEVER set or increment this in application code.
    public byte[] RowVersion { get; set; } = null!;

    // Used as a soft-delete guard in raw SQL queries (UPDLOCK/ROWLOCK).
    // Ensures raw SQL bypasses do not silently return deleted accounts.
    public bool IsDeleted { get; set; } = false;

    public DateTimeOffset UpdatedAt { get; set; }
}
