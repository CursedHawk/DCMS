extern alias AdminApiApp;
extern alias CliApp;
using Dcms.Shared.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.IntegrationTests.Security;

/// <summary>
/// The guard that keeps the operation catalog (ADR 0032) from falling behind the console.
///
/// <para>Desktop agents reach only routes marked <c>.Cli(...)</c>, and the dcms command line and its
/// MCP server learn every operation from those marks. A new console route nobody marks is a
/// feature the command line and agents silently lack. So every route under an area the catalog
/// covers must say <c>.Cli(...)</c> or <c>.NotForCli("why")</c>. Areas join
/// <see cref="CoveredPrefixes"/> wave by wave (C3, C6) until the whole admin API is covered.</para>
///
/// <para>Container-free, like the permission and audit coverage guards: it needs the endpoint table,
/// not a database.</para>
/// </summary>
public sealed class CliCoverageTests : IDisposable
{
    private readonly WebApplicationFactory<AdminApiApp::Program> _factory =
        new WebApplicationFactory<AdminApiApp::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Tenancy:Migrate", "false");
            builder.UseSetting("Tenancy:ApplyRls", "false");
            builder.UseSetting("ConnectionStrings:Postgres", "Host=localhost;Database=unused;Username=unused;Password=unused");
            builder.UseSetting("ConnectionStrings:Redis", "localhost:1");
            builder.UseSetting("Nats:Url", "nats://localhost:1");
        });

    /// <summary>The areas every route of which has decided whether it is in the catalog.</summary>
    public static readonly string[] CoveredPrefixes =
    [
        // C3, wave 1: the console assistant's own reach, sites and apps' code, media.
        "/api/admin/content",
        "/api/admin/media",
        "/api/admin/domains",
        "/api/admin/connections",
        "/api/admin/jobs",
        "/api/admin/audit",
        "/api/admin/usage",
        "/api/admin/analytics",
        "/api/admin/environments",
        "/api/admin/sites",
        "/api/admin/site-runtime",
        "/api/admin/api-fingerprint",
        "/api/admin/repos",
        "/api/admin/plugins/catalog",
        "/api/admin/plugins/instances",
        "/api/admin/plugins/{slug}/_functions",
        "/api/admin/contracts",
        "/api/admin/cli",
        "/api/admin/me",
    ];

    public void Dispose()
    {
        _factory.Dispose();
        GC.SuppressFinalize(this);
    }

    private IReadOnlyList<RouteEndpoint> Endpoints() =>
        [.. _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()];

    private static string PathOf(RouteEndpoint e) => "/" + e.RoutePattern.RawText!.TrimStart('/');

    private static bool Covered(string path) =>
        CoveredPrefixes.Any(p => path == p || path.StartsWith(p + "/", StringComparison.Ordinal));

    private static string Describe(RouteEndpoint e) =>
        $"{string.Join(",", e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.IHttpMethodMetadata>()?.HttpMethods ?? ["*"])} {PathOf(e)}";

    [Fact]
    public void Every_route_in_a_covered_area_says_whether_it_is_in_the_catalog()
    {
        var undecided = Endpoints()
            .Where(e => Covered(PathOf(e)))
            .Where(e => e.Metadata.GetMetadata<CliOperationMetadata>() is null && e.Metadata.GetMetadata<NotForCliMetadata>() is null)
            .Select(Describe).Order().ToList();
        undecided.Should().BeEmpty(
            "every route under a covered area says .Cli(name, summary, risk) or .NotForCli(\"why\") — "
            + "otherwise the dcms command line and desktop agents silently lack it");
    }

    [Fact]
    public void Every_operation_has_one_name_and_says_what_it_does()
    {
        var operations = Endpoints().Select(e => (Endpoint: e, Cli: e.Metadata.GetMetadata<CliOperationMetadata>())).Where(x => x.Cli is not null).ToList();
        operations.Should().NotBeEmpty();
        operations.GroupBy(x => x.Cli!.Name).Where(g => g.Count() > 1).Select(g => g.Key).Should().BeEmpty("an operation's name is how a client calls it");
        operations.Where(x => !System.Text.RegularExpressions.Regex.IsMatch(x.Cli!.Name, "^[a-z][a-z0-9-]*(\\.[a-z][a-z0-9-]*)+$"))
            .Select(x => x.Cli!.Name).Should().BeEmpty("names are dotted lowercase: area.verb");
        operations.Where(x => x.Cli!.Summary.Length < 15).Select(x => x.Cli!.Name).Should().BeEmpty("a summary is what an agent reads to choose");
        operations.Where(x => x.Cli!.Risk == CliRisk.Read
                              && x.Endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.IHttpMethodMetadata>()?.HttpMethods.Contains("DELETE") == true)
            .Select(x => x.Cli!.Name).Should().BeEmpty("a DELETE is never a read");
    }

    [Fact]
    public void Every_core_mcp_tool_names_an_operation_that_exists()
    {
        var names = Endpoints().Select(e => e.Metadata.GetMetadata<CliOperationMetadata>()?.Name).OfType<string>().ToHashSet();
        CliApp::Dcms.Cli.Commands.CoreTools.Where(t => !names.Contains(t)).Should().BeEmpty(
            "`dcms mcp` offers these as tools of their own; a renamed operation would quietly drop one");
    }

    [Fact]
    public void Not_for_cli_says_why()
    {
        Endpoints().Select(e => e.Metadata.GetMetadata<NotForCliMetadata>()).OfType<NotForCliMetadata>()
            .Where(m => m.Reason.Length < 15).Should().BeEmpty();
    }
}
