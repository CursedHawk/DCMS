using Dcms.IntegrationTests.Tenancy;
using Dcms.Shared.Data.Rls;
using Dcms.Shared.Data.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Dcms.IntegrationTests.Environments;

/// <summary>
/// Copying an environment's rows while the source stays live (ADR 0028): a row added mid-copy
/// never makes another one be copied twice.
/// </summary>
[Collection(AdminApiCollection.Name)]
public sealed class EnvironmentRowsTests(AdminApiFixture fixture)
{
    [DockerFact]
    public async Task A_row_added_to_the_source_mid_copy_does_not_copy_another_twice()
    {
        var ct = TestContext.Current.CancellationToken;
        var (tenant, source, target) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        TenancyDbContext Db() => new(new DbContextOptionsBuilder<TenancyDbContext>().UseNpgsql(fixture.PostgresConnectionString).Options);
        Domain Row(Guid id) => new() { Id = id, TenantId = tenant, EnvironmentId = source, Hostname = $"{id:N}.rows.test", VerificationToken = "t" };

        await using (var seed = Db())
        {
            seed.Domains.AddRange(Enumerable.Range(0, 5).Select(_ => Row(Guid.NewGuid())));
            await seed.SaveChangesAsync(ct);
        }

        await using var db = Db();
        var added = false;
        var copied = await EnvironmentRows.CopyAsync(db,
            db.Domains.IgnoreQueryFilters().Where(d => d.TenantId == tenant && d.EnvironmentId == source).OrderBy(d => d.Id),
            () => RlsScope.Environment(tenant, source), () => RlsScope.Environment(tenant, target),
            d =>
            {
                if (!added)
                {
                    // Sorts before every row: an offset would now land one row back.
                    using var other = Db();
                    other.Domains.Add(Row(Guid.Parse("00000000-0000-0000-0000-000000000001")));
                    other.SaveChanges();
                    added = true;
                }
                (d.Id, d.EnvironmentId, d.Hostname) = (Guid.NewGuid(), target, "copy-" + d.Hostname);
                return true;
            }, ct, pageSize: 2);

        copied.Should().Be(5);
        await using var check = Db();
        (await check.Domains.IgnoreQueryFilters().CountAsync(d => d.TenantId == tenant && d.EnvironmentId == target, ct)).Should().Be(5);
    }
}
