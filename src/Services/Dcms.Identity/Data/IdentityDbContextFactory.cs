using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Dcms.Identity.Data;

/// <summary>
/// Design-time factory so `dotnet ef` can build the context without booting the
/// app (and its seeder). The connection string is only used to scaffold SQL;
/// the runtime string comes from configuration/Vault.
/// </summary>
public sealed class IdentityDbContextFactory : IDesignTimeDbContextFactory<IdentityDbContext>
{
    public IdentityDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql(
                "Host=localhost;Port=5432;Database=dcms;Username=dcms;Password=dcms-dev",
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", IdentityDbContext.Schema))
            .UseOpenIddict()
            .Options;

        return new IdentityDbContext(options);
    }
}
