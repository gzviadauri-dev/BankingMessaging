using BankingMessaging.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BankingMessaging.Api.Controllers;

[ApiController]
[Route("api/accounts")]
public class AccountsController : ControllerBase
{
    private readonly BankingDbContext _db;
    private readonly ILogger<AccountsController> _logger;

    public AccountsController(BankingDbContext db, ILogger<AccountsController> logger)
    {
        _db = db;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> GetAccounts(CancellationToken ct)
    {
        var accounts = await _db.Accounts
            .OrderBy(a => a.AccountId)
            .ToListAsync(ct);
        return Ok(accounts);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetAccount(string id, CancellationToken ct)
    {
        var account = await _db.Accounts.FindAsync([id], ct);
        return account is null ? NotFound() : Ok(account);
    }
}
