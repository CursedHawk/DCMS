extern alias AdminApiApp;
extern alias PlatformApiApp;

using System.Reflection;
using System.Text.RegularExpressions;
using Dcms.Shared.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.IntegrationTests.Console;

/// <summary>
/// The console's API surface, checked against the two things it has to agree with: the
/// permission catalogue on this side, and admin-api's route table on the other.
///
/// <para>Container-free, like <c>AuditCoverageTests</c> — this needs the endpoint tables, not a
/// working database, and both failures it guards are silent. A console route that forgot its
/// permission is a page any signed-in user can reach; a delegated route naming an admin-api
/// path that does not exist is a 404 the console renders as an empty page, and neither shows up
/// until somebody clicks the button.</para>
/// </summary>
public sealed class DelegatedConsoleRouteTests : IDisposable
{
    private readonly WebApplicationFactory<PlatformApiApp::Program> _platform = Host<PlatformApiApp::Program>();
    private readonly WebApplicationFactory<AdminApiApp::Program> _admin = Host<AdminApiApp::Program>();

    private static WebApplicationFactory<T> Host<T>() where T : class =>
        new WebApplicationFactory<T>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Tenancy:Migrate", "false");
            builder.UseSetting("Tenancy:ApplyRls", "false");
            builder.UseSetting("Identity:Migrate", "false");
            builder.UseSetting("Identity:Seed", "false");
            builder.UseSetting(
                "ConnectionStrings:Postgres",
                "Host=localhost;Database=unused;Username=unused;Password=unused");
            builder.UseSetting("ConnectionStrings:Redis", "localhost:1");
            builder.UseSetting("Nats:Url", "nats://localhost:1");
        });

    public void Dispose()
    {
        _platform.Dispose();
        _admin.Dispose();
    }

    /// <summary>
    /// Every route the console's browser can reach names a permission. The console is a
    /// cross-tenant operator surface, so an unguarded route here is not "one page leaked" — it
    /// is the tenant list, or the certificate delete button, open to anyone who can sign in.
    /// </summary>
    /// <summary>
    /// The one route that legitimately has no permission: it is how the SPA finds out which
    /// permissions the caller holds, so gating it on one would be circular.
    /// </summary>
    private const string PermissionDiscoveryRoute = "/api/platform/me";

    /// <summary>
    /// The SignalR transport, which cannot carry endpoint permission metadata usefully: a hub
    /// serves many messages over one connection, and the decision it actually makes — whether
    /// this caller joins the broadcast group — needs the permission resolver and so lives in
    /// <c>PlatformHub.OnConnectedAsync</c>. It is still authenticated; see the test below.
    /// </summary>
    private const string HubRoutePrefix = "/api/platform/hub/";

    [Fact]
    public void Every_console_route_names_a_platform_permission()
    {
        var unguarded = Endpoints(_platform)
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api/platform/", StringComparison.Ordinal) == true)
            .Where(e => e.RoutePattern.RawText != PermissionDiscoveryRoute)
            .Where(e => e.RoutePattern.RawText?.StartsWith(HubRoutePrefix, StringComparison.Ordinal) != true)
            .Where(e => e.Metadata.GetMetadata<PlatformPermissionMetadata>() is null)
            .Select(e => e.RoutePattern.RawText!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        unguarded.Should().BeEmpty(
            "the platform console has no tenant scoping to fall back on: a route with no "
            + "permission is open to every signed-in user. Unguarded:\n{0}",
            string.Join("\n", unguarded));
    }

    /// <summary>
    /// The hub is exempt from the permission check above because it makes its own on connect —
    /// but it must never be exempt from authentication. An anonymous WebSocket into the console's
    /// broadcast group would be the one place a resource tag reached somebody with no account.
    /// </summary>
    [Fact]
    public void The_console_hub_refuses_an_anonymous_caller()
    {
        var hub = Endpoints(_platform)
            .Where(e => e.RoutePattern.RawText?.StartsWith(HubRoutePrefix, StringComparison.Ordinal) == true)
            .ToList();

        hub.Should().NotBeEmpty("the console hub is mapped under this prefix");

        foreach (var endpoint in hub)
        {
            endpoint.Metadata.GetMetadata<IAllowAnonymous>().Should().BeNull(
                "{0} would accept a caller with no account", endpoint.RoutePattern.RawText);
            endpoint.Metadata.GetMetadata<IAuthorizeData>().Should().NotBeNull(
                "{0} carries the console's live updates", endpoint.RoutePattern.RawText);
        }
    }

    /// <summary>
    /// Every permission a console route names is a real key. A typo here fails closed rather
    /// than open — the policy never matches, so the page 403s for everyone including a
    /// SuperAdmin — which is safe and completely opaque from the console.
    /// </summary>
    [Fact]
    public void Every_permission_a_route_names_is_in_the_catalogue()
    {
        var unknown = Endpoints(_platform)
            .Select(e => e.Metadata.GetMetadata<PlatformPermissionMetadata>()?.Permission)
            .Where(p => p is not null)
            .Distinct(StringComparer.Ordinal)
            .Where(p => !PlatformConsolePermissions.All.Contains(p!, StringComparer.Ordinal))
            .ToList();

        unknown.Should().BeEmpty("unknown keys: {0}", string.Join(", ", unknown));
    }

    /// <summary>
    /// Each delegated route points at an admin-api route that exists.
    ///
    /// <para>The two services are separate projects with no shared route constants, so the
    /// upstream paths are string literals — and a wrong one is not a compile error, not a
    /// startup error, and not visible until an operator presses the button and gets an empty
    /// page. Reading them out of the source and matching them against admin-api's own endpoint
    /// table is what closes that gap.</para>
    /// </summary>
    [Fact]
    public void Every_delegated_route_points_at_an_admin_api_route_that_exists()
    {
        var upstream = UpstreamPaths();
        upstream.Should().NotBeEmpty("the delegated routes are declared in this file, so an "
                                     + "empty list means the parse stopped matching them");

        var admin = Endpoints(_admin)
            .Select(e => Normalize(e.RoutePattern.RawText ?? string.Empty))
            .ToHashSet(StringComparer.Ordinal);

        var missing = upstream.Where(p => !admin.Contains(p)).OrderBy(x => x, StringComparer.Ordinal).ToList();

        missing.Should().BeEmpty(
            "each of these is a path the console forwards to. Not served by admin-api:\n{0}",
            string.Join("\n", missing));
    }

    /// <summary>
    /// Every tag the console maps to query keys is a tag the server can actually push.
    ///
    /// <para>The two lists are duplicated across the boundary deliberately — a tag either side
    /// does not recognise degrades to "nothing refetches", which is what lets the console and
    /// its API be deployed independently. That same tolerance is what makes a typo invisible:
    /// the console would simply never refresh that page and nothing anywhere would complain.
    /// This is the check that a mapped tag is a real one.</para>
    /// </summary>
    [Fact]
    public void Every_tag_the_console_maps_is_one_the_server_can_push()
    {
        var served = typeof(PlatformApiApp::Dcms.PlatformApi.Realtime.PlatformResourceTags)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);

        var mapped = MappedTags();
        mapped.Should().NotBeEmpty("the map is declared in liveMap.ts, so an empty list means "
                                   + "the parse stopped matching it");

        mapped.Where(t => !served.Contains(t)).Should().BeEmpty(
            "a tag the server never sends silently means that page never refreshes");
    }

    // ---------- helpers ----------

    /// <summary>The keys of `LIVE_QUERY_MAP` in the console's live module.</summary>
    private static List<string> MappedTags()
    {
        var source = ReadRepoFile("apps/platform/src/features/live/liveMap.ts");
        var body = source[source.IndexOf("LIVE_QUERY_MAP", StringComparison.Ordinal)..];

        return [.. Regex.Matches(body, @"^\s{2}([A-Za-z][A-Za-z0-9]*):", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)];
    }

    private static IEnumerable<RouteEndpoint> Endpoints<T>(WebApplicationFactory<T> factory) where T : class =>
        factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();

    /// <summary>
    /// The <c>"/api/admin/..."</c> literals in the delegation source, with their <c>{0}</c>
    /// format slot reduced to the same placeholder a route parameter becomes.
    /// </summary>
    private static string ReadRepoFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Dcms.sln")))
        {
            directory = directory.Parent;
        }
        Assert.SkipWhen(directory is null, "Repository root not found; the source tree is not available here.");
        return File.ReadAllText(Path.Combine(directory!.FullName, relativePath));
    }

    private static List<string> UpstreamPaths()
    {
        var source = ReadRepoFile("src/Services/Dcms.PlatformApi/Delegation/DelegatedConsoleEndpoints.cs");

        return [.. Regex.Matches(source, @"""(/api/admin/[^""]*)""")
            .Select(m => m.Groups[1].Value.Replace("{0}", "{}", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)];
    }

    /// <summary>A route pattern with its parameters and their constraints reduced to <c>{}</c>.</summary>
    private static string Normalize(string pattern) =>
        "/" + Regex.Replace(pattern, @"\{[^}]*\}", "{}").TrimStart('/');
}
