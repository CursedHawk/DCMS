using Dcms.Edge.Certificates;
using Dcms.Shared.Data.Edge;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Dcms.UnitTests.Edge;

/// <summary>
/// The Caddy import runs before Kestrel starts listening, on the public ingress. What it must
/// never do is stop the edge from starting.
/// </summary>
public class CaddyImportStartupTests
{
    [Fact]
    public async Task Gives_up_quietly_when_the_database_is_unreachable()
    {
        var collection = new ServiceCollection();
        collection.AddDbContext<EdgeDbContext>(options =>
            // A port nothing is listening on, which is what a Postgres restart looks like from
            // here.
            options.UseNpgsql("Host=127.0.0.1;Port=1;Database=dcms;Username=dcms_edge;Password=x;Timeout=1"));
        var services = collection.BuildServiceProvider();

        var caddyData = Directory.CreateTempSubdirectory("caddy-import-startup").FullName;
        Directory.CreateDirectory(Path.Combine(caddyData, "caddy", "certificates"));

        var importer = new CaddyCertificateImporter(
            services, Substitute.For<ICertificateStore>(),
            NullLogger<CaddyCertificateImporter>.Instance);

        try
        {
            // The edge serves every route from its static table with Postgres down -- that is
            // deliberate and tested. Refusing to start over an OPTIONAL import would turn a
            // database blip into a total outage of the public ingress, on the one deploy where
            // everybody is already watching something else.
            var import = async () => await importer.ImportAsync(caddyData, TestContext.Current.CancellationToken);
            await import.Should().NotThrowAsync();
        }
        finally
        {
            Directory.Delete(caddyData, recursive: true);
            await services.DisposeAsync();
        }
    }
}
