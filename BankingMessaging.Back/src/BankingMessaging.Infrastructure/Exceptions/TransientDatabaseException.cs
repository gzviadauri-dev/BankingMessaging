namespace BankingMessaging.Infrastructure.Exceptions;

public class TransientDatabaseException : Exception
{
    public TransientDatabaseException(string message) : base(message) { }
    public TransientDatabaseException(string message, Exception innerException) : base(message, innerException) { }
}
