namespace BankingMessaging.Infrastructure.Exceptions;

public class InsufficientFundsException : Exception
{
    public string AccountId { get; }
    public decimal RequestedAmount { get; }

    public InsufficientFundsException(string accountId, decimal amount)
        : base($"Account {accountId} has insufficient funds for {amount}")
    {
        AccountId = accountId;
        RequestedAmount = amount;
    }
}
