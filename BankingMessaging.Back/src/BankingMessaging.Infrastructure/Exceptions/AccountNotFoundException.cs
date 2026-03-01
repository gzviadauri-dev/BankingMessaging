namespace BankingMessaging.Infrastructure.Exceptions;

public class AccountNotFoundException : Exception
{
    public string AccountId { get; }

    public AccountNotFoundException(string accountId)
        : base($"Account {accountId} not found")
    {
        AccountId = accountId;
    }
}
