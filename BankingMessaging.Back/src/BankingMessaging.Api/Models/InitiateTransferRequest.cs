using System.ComponentModel.DataAnnotations;

namespace BankingMessaging.Api.Models;

public record InitiateTransferRequest
{
    [Required]
    public string FromAccountId { get; init; } = default!;

    [Required]
    public string ToAccountId { get; init; } = default!;

    [Range(0.01, double.MaxValue, ErrorMessage = "Amount must be greater than zero.")]
    public decimal Amount { get; init; }

    public string Currency { get; init; } = "USD";

    public bool SimulateError { get; init; }
}
