using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BankingMessaging.Infrastructure.Persistence;

/// <summary>
/// Required for EF Core CLI tools (dotnet ef migrations) when running
/// from the Infrastructure project without a startup project that registers the DbContext.
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<BankingDbContext>
{
    public BankingDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<BankingDbContext>();
        optionsBuilder.UseSqlServer(
            "Server=localhost,1433;Database=banking;User Id=SA;Password=Banking123!;TrustServerCertificate=True;");

        return new BankingDbContext(optionsBuilder.Options);
    }
}
