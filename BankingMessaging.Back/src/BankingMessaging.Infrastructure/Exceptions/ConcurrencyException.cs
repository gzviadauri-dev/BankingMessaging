namespace BankingMessaging.Infrastructure.Exceptions;

public class ConcurrencyException : Exception
{
    public ConcurrencyException(string message) : base(message) { }
}
