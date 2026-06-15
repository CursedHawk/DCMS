using Dcms.Shared.Kernel.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Dcms.Shared.Data.Sites;

public sealed class SitesDbContextFactory : IDesignTimeDbContextFactory<SitesDbContext>
{
    public SitesDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<SitesDbContext>()
            .UseNpgsql(
                "Host=localhost;Port=5432;Database=dcms;Username=dcms;Password=dcms-dev",
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", SitesDbContext.Schema))
            .Options;

        return new SitesDbContext(options, new NullTenantContext());
    }

    private sealed class NullTenantContext : ITenantContext
    {
        public Guid? TenantId => null;
        public string? TenantSlug => null;
    }
}
