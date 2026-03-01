using BankingMessaging.Api.Models;
using BankingMessaging.Contracts.Commands;
using BankingMessaging.Infrastructure.Entities;
using BankingMessaging.Infrastructure.Persistence;
using MassTransit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TransferStatus = BankingMessaging.Infrastructure.Entities.TransferStatus;

namespace BankingMessaging.Api.Controllers;

[ApiController]
[Route("api/transfers")]
public class TransferController : ControllerBase
{
    private readonly IPublishEndpoint _publishEndpoint;
    private readonly BankingDbContext _db;
    private readonly ILogger<TransferController> _logger;

    public TransferController(
        IPublishEndpoint publishEndpoint,
        BankingDbContext db,
        ILogger<TransferController> logger)
    {
        _publishEndpoint = publishEndpoint;
        _db = db;
        _logger = logger;
    }

    [HttpPost]
    public async Task<IActionResult> InitiateTransfer(
        [FromBody] InitiateTransferRequest request,
        CancellationToken ct)
    {
        var transferId = NewId.NextGuid();
        var correlationId = NewId.NextGuid();

        _logger.LogInformation(
            "Initiating transfer {TransferId} from {From} to {To} for {Amount} {Currency}. CorrelationId={CorrelationId}",
            transferId, request.FromAccountId, request.ToAccountId,
            request.Amount, request.Currency, correlationId);

        var transfer = new Transfer
        {
            TransferId = transferId,
            FromAccountId = request.FromAccountId,
            ToAccountId = request.ToAccountId,
            Amount = request.Amount,
            Currency = request.Currency,
            Status = TransferStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow
        };

        _db.Transfers.Add(transfer);

        // Publish goes to the outbox table — only reaches RabbitMQ after DB commit
        await _publishEndpoint.Publish(new InitiateTransferCommand
        {
            TransferId = transferId,
            CorrelationId = correlationId,
            FromAccountId = request.FromAccountId,
            ToAccountId = request.ToAccountId,
            Amount = request.Amount,
            Currency = request.Currency,
            RequestedBy = User.Identity?.Name ?? "anonymous",
            RequestedAt = DateTimeOffset.UtcNow,
            SimulateError = request.SimulateError
        }, ct);

        await _db.SaveChangesAsync(ct);

        return Accepted(new { transferId, correlationId });
    }

    [HttpGet("{transferId:guid}")]
    public async Task<IActionResult> GetTransfer(Guid transferId, CancellationToken ct)
    {
        var transfer = await _db.Transfers.FindAsync([transferId], ct);
        if (transfer is null)
            return NotFound();

        return Ok(transfer);
    }

    [HttpGet]
    public async Task<IActionResult> ListTransfers([FromQuery] string? status, CancellationToken ct)
    {
        var query = _db.Transfers.AsQueryable();
        if (status is not null)
            query = query.Where(t => t.Status == status);

        var transfers = await query
            .OrderByDescending(t => t.CreatedAt)
            .Take(50)
            .ToListAsync(ct);

        return Ok(transfers);
    }

    [HttpGet("accounts/{accountId}")]
    public async Task<IActionResult> GetAccount(string accountId, CancellationToken ct)
    {
        var account = await _db.Accounts.FindAsync([accountId], ct);
        if (account is null)
            return NotFound();

        return Ok(account);
    }
}
